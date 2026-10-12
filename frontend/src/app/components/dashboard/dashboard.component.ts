import { Component, OnInit } from '@angular/core';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { AuthService } from '../../services/auth.service';
import { AccountService } from '../../services/account.service';
import { User } from '../../models/auth.model';
import { Account, Transaction } from '../../models/account.model';

interface BalanceByCurrency {
    currency: string;
    total: number;
}

@Component({
    selector: 'app-dashboard',
    templateUrl: './dashboard.component.html',
    styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
    user: User | null = null;
    loading = true;
    failed = false;

    accounts: Account[] = [];
    balances: BalanceByCurrency[] = [];
    activeAccounts = 0;
    totalTransactions = 0;
    recent: Transaction[] = [];

    constructor(
        private authService: AuthService,
        private accountService: AccountService
    ) { }

    ngOnInit(): void {
        this.user = this.authService.currentUserValue;
        this.load();
    }

    load(): void {
        this.loading = true;
        this.failed = false;

        this.accountService.getAccounts().subscribe({
            next: accounts => {
                this.accounts = accounts;
                this.activeAccounts = accounts.filter(a => a.status === 'Active').length;
                this.balances = this.sumByCurrency(accounts);
                this.loadActivity(accounts);
            },
            error: () => {
                this.loading = false;
                this.failed = true;
            }
        });
    }

    private loadActivity(accounts: Account[]): void {
        if (accounts.length === 0) {
            this.totalTransactions = 0;
            this.recent = [];
            this.loading = false;
            return;
        }

        forkJoin(
            accounts.map(account =>
                this.accountService.getTransactions(account.id, 1, 5).pipe(
                    catchError(() => of(null))
                )
            )
        ).pipe(
            map(pages => pages.filter((page): page is NonNullable<typeof page> => page !== null))
        ).subscribe(pages => {
            this.totalTransactions = pages.reduce((sum, page) => sum + page.totalCount, 0);
            this.recent = pages
                .flatMap(page => page.items)
                .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
                .slice(0, 8);
            this.loading = false;
        });
    }

    private sumByCurrency(accounts: Account[]): BalanceByCurrency[] {
        const totals = new Map<string, number>();
        for (const account of accounts) {
            totals.set(account.currency, (totals.get(account.currency) ?? 0) + account.balance);
        }
        return Array.from(totals, ([currency, total]) => ({ currency, total }));
    }
}
