import { Component, OnInit } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AccountService } from '../../services/account.service';
import { Account } from '../../models/account.model';

@Component({
    selector: 'app-accounts',
    templateUrl: './accounts.component.html',
    styleUrls: ['./accounts.component.scss']
})
export class AccountsComponent implements OnInit {
    accounts: Account[] = [];
    loading = false;
    displayedColumns: string[] = ['accountNumber', 'accountType', 'balance', 'currency', 'status'];

    constructor(
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
            error: (error) => {
                this.loading = false;
                this.snackBar.open('Failed to load accounts', 'Close', { duration: 3000 });
            }
        });
    }

    createAccount(): void {
        this.accountService.createAccount({ accountType: 'Savings', currency: 'USD' }).subscribe({
            next: () => {
                this.snackBar.open('Account created successfully', 'Close', { duration: 3000 });
                this.loadAccounts();
            },
            error: (error) => {
                this.snackBar.open('Failed to create account', 'Close', { duration: 3000 });
            }
        });
    }
}
