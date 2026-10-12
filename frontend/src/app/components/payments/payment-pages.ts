import { Component, OnInit } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { AccountService } from '../../services/account.service';
import { PaymentsService } from '../../services/payments.service';
import { Account } from '../../models/account.model';
import { PaymentMethod, RecurringPayment } from '../../models/api.models';
import { PAGE_IMPORTS, errorMessage } from '../../shared/material';

const PAGE_STYLES = `
  .page { padding: 24px; max-width: 960px; margin: 0 auto; }
  mat-card { margin-bottom: 24px; }
  .row { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; padding-top: 12px; }
  .full { width: 100%; }
  .muted { color: #777; font-size: 13px; }
  .badge { padding: 2px 10px; border-radius: 10px; font-size: 12px; background: #eee; }
  .good { background: #e8f5e9; color: #2e7d32; }
  .bad { background: #ffebee; color: #c62828; }
`;

/** Saved cards and PayPal accounts. Card numbers are tokenized by the gateway and never stored. */
@Component({
    selector: 'app-payment-methods',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [PAGE_STYLES],
    template: `
    <div class="page">
      <h1>Payment methods</h1>

      <mat-card>
        <mat-card-header>
          <mat-card-title>Add a card or PayPal account</mat-card-title>
          <mat-card-subtitle>Used to top up your accounts. Only the token, brand, last four digits and expiry are saved.</mat-card-subtitle>
        </mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="add()" class="row">
            <mat-form-field appearance="outline">
              <mat-label>Provider</mat-label>
              <mat-select formControlName="provider">
                <mat-option value="Stripe">Stripe (card)</mat-option>
                <mat-option value="PayPal">PayPal</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Card number</mat-label>
              <input matInput formControlName="cardNumber" inputmode="numeric" autocomplete="cc-number" />
            </mat-form-field>
            <mat-form-field appearance="outline" style="width: 90px"><mat-label>MM</mat-label><input matInput type="number" formControlName="expMonth" min="1" max="12" /></mat-form-field>
            <mat-form-field appearance="outline" style="width: 110px"><mat-label>YYYY</mat-label><input matInput type="number" formControlName="expYear" /></mat-form-field>
            <mat-form-field appearance="outline" style="width: 100px"><mat-label>CVC</mat-label><input matInput formControlName="cvc" maxlength="4" autocomplete="cc-csc" /></mat-form-field>
            <mat-checkbox formControlName="makeDefault">Make default</mat-checkbox>
            <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || busy">Add</button>
          </form>
          <p class="muted">
            Test mode: 4242 4242 4242 4242 works, 4000 0000 0000 0002 is declined at charge time, 4000 0000 0000 9995 has insufficient funds.
            With live Stripe or PayPal keys on the server the browser must send the provider's own token instead of a card number.
          </p>
        </mat-card-content>
      </mat-card>

      <mat-card>
        <mat-card-content>
          <p *ngIf="methods.length === 0" class="muted">No saved payment methods yet.</p>
          <table mat-table [dataSource]="methods" class="full" *ngIf="methods.length > 0">
            <ng-container matColumnDef="method"><th mat-header-cell *matHeaderCellDef>Method</th>
              <td mat-cell *matCellDef="let m">{{ m.brand | titlecase }} ···· {{ m.last4 }} <span class="muted">({{ m.provider }})</span>
                <span class="badge good" *ngIf="m.isDefault">Default</span></td></ng-container>
            <ng-container matColumnDef="expiry"><th mat-header-cell *matHeaderCellDef>Expires</th><td mat-cell *matCellDef="let m">{{ m.expMonth }}/{{ m.expYear }}</td></ng-container>
            <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let m">
                <button mat-button *ngIf="!m.isDefault" (click)="makeDefault(m)">Make default</button>
                <button mat-button color="warn" (click)="remove(m)">Remove</button>
              </td></ng-container>
            <tr mat-header-row *matHeaderRowDef="['method','expiry','actions']"></tr>
            <tr mat-row *matRowDef="let row; columns: ['method','expiry','actions']"></tr>
          </table>
        </mat-card-content>
      </mat-card>
    </div>
  `
})
export class PaymentMethodsComponent implements OnInit {
    methods: PaymentMethod[] = [];
    busy = false;

    form = this.fb.nonNullable.group({
        provider: ['Stripe', Validators.required],
        cardNumber: ['', Validators.required],
        expMonth: [12, [Validators.required, Validators.min(1), Validators.max(12)]],
        expYear: [new Date().getFullYear() + 3, Validators.required],
        cvc: [''],
        makeDefault: [false]
    });

    constructor(private fb: FormBuilder, private payments: PaymentsService, private snackBar: MatSnackBar) { }

    ngOnInit(): void {
        this.load();
    }

    load(): void {
        this.payments.getMethods().subscribe(methods => (this.methods = methods));
    }

    add(): void {
        if (this.form.invalid || this.busy) { return; }
        this.busy = true;
        this.payments.addMethod(this.form.getRawValue()).subscribe({
            next: () => {
                this.busy = false;
                this.form.patchValue({ cardNumber: '', cvc: '', makeDefault: false });
                this.snackBar.open('Payment method added', 'Close', { duration: 3000 });
                this.load();
            },
            error: error => {
                this.busy = false;
                this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 });
            }
        });
    }

    makeDefault(method: PaymentMethod): void {
        this.payments.makeDefault(method.id).subscribe({ next: () => this.load(), error: e => this.snackBar.open(errorMessage(e), 'Close', { duration: 4000 }) });
    }

    remove(method: PaymentMethod): void {
        if (!confirm(`Remove ${method.brand} ending ${method.last4}?`)) { return; }
        this.payments.removeMethod(method.id).subscribe({ next: () => this.load(), error: e => this.snackBar.open(errorMessage(e), 'Close', { duration: 4000 }) });
    }
}

/** Standing instructions to pay a merchant daily, weekly or monthly. */
@Component({
    selector: 'app-recurring-payments',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [PAGE_STYLES],
    template: `
    <div class="page">
      <h1>Recurring payments</h1>

      <mat-card>
        <mat-card-header><mat-card-title>New standing payment</mat-card-title></mat-card-header>
        <mat-card-content>
          <form [formGroup]="form" (ngSubmit)="create()" class="row">
            <mat-form-field appearance="outline">
              <mat-label>From account</mat-label>
              <mat-select formControlName="accountId">
                <mat-option *ngFor="let a of accounts" [value]="a.id">{{ a.accountType }} {{ a.accountNumber | slice: 0 : 8 }}… ({{ a.balance | currency: a.currency }})</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Merchant</mat-label><input matInput formControlName="merchantName" /></mat-form-field>
            <mat-form-field appearance="outline" style="width: 130px"><mat-label>Amount</mat-label><input matInput type="number" step="0.01" formControlName="amount" /></mat-form-field>
            <mat-form-field appearance="outline" style="width: 140px">
              <mat-label>Repeats</mat-label>
              <mat-select formControlName="frequency">
                <mat-option value="Daily">Daily</mat-option>
                <mat-option value="Weekly">Weekly</mat-option>
                <mat-option value="Monthly">Monthly</mat-option>
              </mat-select>
            </mat-form-field>
            <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || busy">Create</button>
          </form>
          <p class="muted">Payments run on the bank's hourly schedule starting at the first due time. Three failed attempts in a row stop the payment.</p>
        </mat-card-content>
      </mat-card>

      <mat-card>
        <mat-card-content>
          <p *ngIf="payments.length === 0" class="muted">No recurring payments.</p>
          <table mat-table [dataSource]="payments" class="full" *ngIf="payments.length > 0">
            <ng-container matColumnDef="merchant"><th mat-header-cell *matHeaderCellDef>Merchant</th><td mat-cell *matCellDef="let p">{{ p.merchantName }}</td></ng-container>
            <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th><td mat-cell *matCellDef="let p">{{ p.amount | currency: p.currency }} {{ p.frequency | lowercase }}</td></ng-container>
            <ng-container matColumnDef="next"><th mat-header-cell *matHeaderCellDef>Next payment</th><td mat-cell *matCellDef="let p">{{ p.status === 'Active' ? (p.nextRunAt | date: 'medium') : '—' }}</td></ng-container>
            <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th>
              <td mat-cell *matCellDef="let p"><span class="badge" [class.good]="p.status === 'Active'" [class.bad]="p.status === 'Failed'">{{ p.status }}</span>
                <div class="muted" *ngIf="p.lastResult">{{ p.lastResult }}</div></td></ng-container>
            <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
              <td mat-cell *matCellDef="let p">
                <button mat-button *ngIf="p.status === 'Active'" (click)="act(p, 'pause')">Pause</button>
                <button mat-button *ngIf="p.status === 'Paused' || p.status === 'Failed'" (click)="act(p, 'resume')">Resume</button>
                <button mat-button color="warn" *ngIf="p.status !== 'Cancelled'" (click)="act(p, 'cancel')">Cancel</button>
              </td></ng-container>
            <tr mat-header-row *matHeaderRowDef="['merchant','amount','next','status','actions']"></tr>
            <tr mat-row *matRowDef="let row; columns: ['merchant','amount','next','status','actions']"></tr>
          </table>
        </mat-card-content>
      </mat-card>
    </div>
  `
})
export class RecurringPaymentsComponent implements OnInit {
    accounts: Account[] = [];
    payments: RecurringPayment[] = [];
    busy = false;

    form = this.fb.nonNullable.group({
        accountId: ['', Validators.required],
        merchantName: ['', Validators.required],
        amount: [null as number | null, [Validators.required, Validators.min(0.01)]],
        frequency: ['Monthly', Validators.required]
    });

    constructor(
        private fb: FormBuilder,
        private paymentsService: PaymentsService,
        private accountService: AccountService,
        private snackBar: MatSnackBar
    ) { }

    ngOnInit(): void {
        this.accountService.getAccounts().subscribe(accounts => {
            this.accounts = accounts.filter(a => a.status === 'Active');
            if (this.accounts.length && !this.form.value.accountId) {
                this.form.patchValue({ accountId: this.accounts[0].id });
            }
        });
        this.load();
    }

    load(): void {
        this.paymentsService.getRecurring().subscribe(payments => (this.payments = payments));
    }

    create(): void {
        if (this.form.invalid || this.busy) { return; }
        const value = this.form.getRawValue();
        this.busy = true;
        this.paymentsService.createRecurring({
            accountId: value.accountId,
            merchantName: value.merchantName,
            amount: value.amount!,
            frequency: value.frequency
        }).subscribe({
            next: () => {
                this.busy = false;
                this.form.patchValue({ merchantName: '', amount: null });
                this.snackBar.open('Recurring payment created', 'Close', { duration: 3000 });
                this.load();
            },
            error: error => {
                this.busy = false;
                this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 });
            }
        });
    }

    act(payment: RecurringPayment, action: 'pause' | 'resume' | 'cancel'): void {
        if (action === 'cancel' && !confirm(`Cancel the payment to ${payment.merchantName}?`)) { return; }
        const call: Observable<void> = this.paymentsService.setRecurring(payment.id, action);
        call.subscribe({ next: () => this.load(), error: e => this.snackBar.open(errorMessage(e), 'Close', { duration: 4000 }) });
    }
}
