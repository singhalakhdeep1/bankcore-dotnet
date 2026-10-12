import { Component, OnInit } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AccountService } from '../../services/account.service';
import { ACCOUNT_TYPES, Account, CURRENCIES } from '../../models/account.model';

@Component({
    selector: 'app-accounts',
    templateUrl: './accounts.component.html',
    styleUrls: ['./accounts.component.scss']
})
export class AccountsComponent implements OnInit {
    accounts: Account[] = [];
    loading = false;
    creating = false;
    showForm = false;
    displayedColumns: string[] = ['accountNumber', 'accountType', 'balance', 'currency', 'status'];

    readonly accountTypes = ACCOUNT_TYPES;
    readonly currencies = CURRENCIES;

    form = this.fb.nonNullable.group({
        accountType: ['Checking', Validators.required],
        currency: ['USD', Validators.required]
    });

    constructor(
        private fb: FormBuilder,
        private router: Router,
        private accountService: AccountService,
        private snackBar: MatSnackBar
    ) { }

    ngOnInit(): void {
        this.loadAccounts();
    }

    loadAccounts(): void {
        this.loading = true;
        this.accountService.getAccounts().subscribe({
            next: (accounts) => {
                this.accounts = accounts;
                this.loading = false;
            },
            error: () => {
                this.loading = false;
                this.snackBar.open('Failed to load accounts', 'Close', { duration: 3000 });
            }
        });
    }

    createAccount(): void {
        if (this.form.invalid || this.creating) {
            return;
        }

        this.creating = true;
        this.accountService.createAccount(this.form.getRawValue()).subscribe({
            next: (account) => {
                this.creating = false;
                this.showForm = false;
                this.snackBar.open('Account created', 'Close', { duration: 3000 });
                this.router.navigate(['/accounts', account.id]);
            },
            error: (error) => {
                this.creating = false;
                this.snackBar.open(error?.error?.message ?? 'Failed to create account', 'Close', { duration: 4000 });
            }
        });
    }

    open(account: Account): void {
        this.router.navigate(['/accounts', account.id]);
    }
}
