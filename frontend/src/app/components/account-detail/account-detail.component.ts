import { Component, OnInit } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { PageEvent } from '@angular/material/paginator';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { AccountService } from '../../services/account.service';
import { Account, Transaction } from '../../models/account.model';

type ActionName = 'deposit' | 'withdraw' | 'transfer' | 'pay';

@Component({
    selector: 'app-account-detail',
    templateUrl: './account-detail.component.html',
    styleUrls: ['./account-detail.component.scss']
})
export class AccountDetailComponent implements OnInit {
    account: Account | null = null;
    loading = true;
    notFound = false;
    reconciled: boolean | null = null;

    transactions: Transaction[] = [];
    totalCount = 0;
    page = 1;
    pageSize = 10;
    displayedColumns = ['createdAt', 'type', 'description', 'amount', 'balanceAfter'];

    busy: ActionName | null = null;

    /** Kept until a transfer succeeds, so retrying after a lost response cannot move money twice. */
    private transferKey = this.newKey();

    private readonly amount = [Validators.required, Validators.min(0.01), Validators.pattern(/^\d+(\.\d{1,2})?$/)];

    depositForm = this.fb.nonNullable.group({ amount: [null as number | null, this.amount], description: [''] });
    withdrawForm = this.fb.nonNullable.group({ amount: [null as number | null, this.amount], description: [''] });
    transferForm = this.fb.nonNullable.group({
        toAccountNumber: ['', Validators.required],
        amount: [null as number | null, this.amount],
        description: ['']
    });
    payForm = this.fb.nonNullable.group({
        merchantName: ['', Validators.required],
        amount: [null as number | null, this.amount],
        description: ['']
    });

    constructor(
        private fb: FormBuilder,
        private route: ActivatedRoute,
        private router: Router,
        private accountService: AccountService,
        private snackBar: MatSnackBar
    ) { }

    ngOnInit(): void {
        const id = this.route.snapshot.paramMap.get('id');
        if (!id) {
            this.router.navigate(['/accounts']);
            return;
        }

        this.accountService.getAccount(id).subscribe({
            next: account => {
                this.account = account;
                this.loading = false;
                this.loadTransactions();
                this.loadLedger();
            },
            error: () => {
                this.loading = false;
                this.notFound = true;
            }
        });
    }

    get canTransact(): boolean {
        return this.account?.status === 'Active';
    }

    loadTransactions(): void {
        if (!this.account) { return; }
        this.accountService.getTransactions(this.account.id, this.page, this.pageSize).subscribe({
            next: result => {
                this.transactions = result.items;
                this.totalCount = result.totalCount;
            },
            error: () => this.snackBar.open('Failed to load transactions', 'Close', { duration: 3000 })
        });
    }

    onPage(event: PageEvent): void {
        this.page = event.pageIndex + 1;
        this.pageSize = event.pageSize;
        this.loadTransactions();
    }

    deposit(): void {
        const account = this.account;
        if (!account || this.depositForm.invalid) { return; }
        const { amount, description } = this.depositForm.getRawValue();
        this.run('deposit', this.accountService.deposit(account.id, amount!, description || undefined),
            `Deposited ${amount}`, () => this.depositForm.reset());
    }

    withdraw(): void {
        const account = this.account;
        if (!account || this.withdrawForm.invalid) { return; }
        const { amount, description } = this.withdrawForm.getRawValue();
        this.run('withdraw', this.accountService.withdraw(account.id, amount!, description || undefined),
            `Withdrew ${amount}`, () => this.withdrawForm.reset());
    }

    transfer(): void {
        const account = this.account;
        if (!account || this.transferForm.invalid) { return; }
        const { toAccountNumber, amount, description } = this.transferForm.getRawValue();
        this.run('transfer', this.accountService.transfer({
            fromAccountId: account.id,
            toAccountNumber: toAccountNumber.trim(),
            amount: amount!,
            description: description || undefined,
            idempotencyKey: this.transferKey
        }), `Transferred ${amount} to ${toAccountNumber.trim()}`, () => {
            this.transferForm.reset();
            this.transferKey = this.newKey();
        });
    }

    pay(): void {
        const account = this.account;
        if (!account || this.payForm.invalid) { return; }
        const { merchantName, amount, description } = this.payForm.getRawValue();
        this.run('pay', this.accountService.pay({
            accountId: account.id,
            amount: amount!,
            merchantName: merchantName.trim(),
            description: description || undefined
        }), `Paid ${merchantName.trim()}`, () => this.payForm.reset());
    }

    private run<T>(action: ActionName, call: Observable<T>, success: string, reset: () => void): void {
        if (this.busy) { return; }
        this.busy = action;

        call.subscribe({
            next: () => {
                this.busy = null;
                reset();
                this.snackBar.open(success, 'Close', { duration: 3000 });
                this.refresh();
            },
            error: error => {
                this.busy = null;
                const message = error?.error?.message ?? error?.error?.errorMessage ?? 'The request failed';
                this.snackBar.open(message, 'Close', { duration: 5000 });
                if (error?.status === 409) { this.refresh(); }
            }
        });
    }

    /** Reload balance, history and the ledger check after any money movement. */
    private refresh(): void {
        if (!this.account) { return; }
        this.accountService.getAccount(this.account.id).subscribe(account => (this.account = account));
        this.page = 1;
        this.loadTransactions();
        this.loadLedger();
    }

    private loadLedger(): void {
        if (!this.account) { return; }
        this.accountService.getLedger(this.account.id).subscribe({
            next: ledger => (this.reconciled = ledger.reconciled),
            error: () => (this.reconciled = null)
        });
    }

    private newKey(): string {
        return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    }
}
