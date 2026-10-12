import { Component, EventEmitter, Input, OnChanges, OnInit, Output } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { PaymentsService } from '../../services/payments.service';
import { Account } from '../../models/account.model';
import { BankTransfer, PaymentMethod, PaymentRecord } from '../../models/api.models';
import { PAGE_IMPORTS, errorMessage } from '../../shared/material';

const PANEL_STYLES = `
  .row { display: flex; flex-wrap: wrap; gap: 12px; align-items: flex-start; padding-top: 16px; }
  .full { width: 100%; }
  .muted { color: #777; font-size: 13px; }
  .badge { padding: 2px 10px; border-radius: 10px; font-size: 12px; background: #eee; }
  .good { background: #e8f5e9; color: #2e7d32; }
  .bad { background: #ffebee; color: #c62828; }
  .note { padding: 8px 0; }
`;

/** Adds money to an account by charging a stored card or PayPal account through the payment gateway. */
@Component({
    selector: 'app-top-up-panel',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [PANEL_STYLES],
    template: `
    <div class="note muted" *ngIf="methods.length === 0">
      You have no saved payment method. <a routerLink="/payment-methods">Add a card</a> first.
    </div>
    <form [formGroup]="form" (ngSubmit)="submit()" class="row" *ngIf="methods.length > 0">
      <mat-form-field appearance="outline">
        <mat-label>Pay with</mat-label>
        <mat-select formControlName="paymentMethodId">
          <mat-option *ngFor="let m of methods" [value]="m.id">{{ m.brand | titlecase }} ···· {{ m.last4 }} ({{ m.provider }})</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Amount ({{ account.currency }})</mat-label>
        <input matInput type="number" step="0.01" formControlName="amount" />
      </mat-form-field>
      <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || busy || account.status !== 'Active'">Top up</button>
    </form>
    <p class="muted" *ngIf="lastMode">Last charge went through {{ lastProvider }} in <strong>{{ lastMode }}</strong> mode.</p>
  `
})
export class TopUpPanelComponent implements OnInit {
    @Input({ required: true }) account!: Account;
    @Output() changed = new EventEmitter<void>();

    methods: PaymentMethod[] = [];
    busy = false;
    lastMode = '';
    lastProvider = '';

    form = this.fb.nonNullable.group({
        paymentMethodId: ['', Validators.required],
        amount: [null as number | null, [Validators.required, Validators.min(0.01)]]
    });

    constructor(private fb: FormBuilder, private payments: PaymentsService, private snackBar: MatSnackBar) { }

    ngOnInit(): void {
        this.payments.getMethods().subscribe(methods => {
            this.methods = methods;
            const preferred = methods.find(m => m.isDefault) ?? methods[0];
            if (preferred) { this.form.patchValue({ paymentMethodId: preferred.id }); }
        });
    }

    submit(): void {
        if (this.form.invalid || this.busy) { return; }
        this.busy = true;
        const { paymentMethodId, amount } = this.form.getRawValue();

        this.payments.topUp(this.account.id, paymentMethodId, amount!).subscribe({
            next: result => {
                this.busy = false;
                this.lastMode = result.mode ?? '';
                this.lastProvider = result.provider ?? '';
                this.form.patchValue({ amount: null });
                this.snackBar.open(`Added ${amount} to the account`, 'Close', { duration: 3000 });
                this.changed.emit();
            },
            error: error => {
                this.busy = false;
                this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 });
            }
        });
    }
}

/** Sends money to another bank by ACH, wire or SWIFT, and lists the account's transfers. */
@Component({
    selector: 'app-bank-transfer-panel',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [PANEL_STYLES],
    template: `
    <form [formGroup]="form" (ngSubmit)="submit()" class="row">
      <mat-form-field appearance="outline">
        <mat-label>Type</mat-label>
        <mat-select formControlName="rail">
          <mat-option value="Ach">ACH (USD, ~2 business days, fee 0.50)</mat-option>
          <mat-option value="Wire">Domestic wire (USD, same day, fee 15)</mat-option>
          <mat-option value="Swift">International SWIFT (fee 35 + 0.1%)</mat-option>
        </mat-select>
      </mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Beneficiary name</mat-label><input matInput formControlName="beneficiaryName" /></mat-form-field>

      <ng-container *ngIf="form.value.rail !== 'Swift'">
        <mat-form-field appearance="outline"><mat-label>Routing number (ABA)</mat-label><input matInput formControlName="routingNumber" maxlength="9" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Account number</mat-label><input matInput formControlName="beneficiaryAccountNumber" /></mat-form-field>
      </ng-container>
      <ng-container *ngIf="form.value.rail === 'Swift'">
        <mat-form-field appearance="outline"><mat-label>SWIFT / BIC</mat-label><input matInput formControlName="swiftBic" maxlength="11" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>IBAN</mat-label><input matInput formControlName="iban" /></mat-form-field>
        <mat-form-field appearance="outline" style="width: 110px"><mat-label>Country</mat-label><input matInput formControlName="destinationCountry" maxlength="2" /></mat-form-field>
      </ng-container>

      <mat-form-field appearance="outline">
        <mat-label>Amount ({{ account.currency }})</mat-label>
        <input matInput type="number" step="0.01" formControlName="amount" />
        <mat-hint *ngIf="fee !== null">Fee {{ fee | currency: account.currency }} · total {{ (form.value.amount ?? 0) + fee | currency: account.currency }}</mat-hint>
      </mat-form-field>
      <button mat-raised-button color="primary" type="submit" [disabled]="form.invalid || busy || account.status !== 'Active'">Send</button>
    </form>

    <table mat-table [dataSource]="transfers" class="full" *ngIf="transfers.length > 0">
      <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Submitted</th><td mat-cell *matCellDef="let t">{{ t.submittedAt | date: 'short' }}</td></ng-container>
      <ng-container matColumnDef="to"><th mat-header-cell *matHeaderCellDef>To</th><td mat-cell *matCellDef="let t">{{ t.beneficiaryName }} <span class="muted">({{ t.rail }})</span></td></ng-container>
      <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th><td mat-cell *matCellDef="let t">{{ t.amount | currency: t.currency }} <span class="muted">+ {{ t.fee | currency: t.currency }} fee</span></td></ng-container>
      <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let t"><span class="badge" [class.good]="t.status === 'Settled'" [class.bad]="t.status === 'Returned' || t.status === 'Failed'">{{ t.status }}</span>
          <div class="muted" *ngIf="t.status !== 'Settled' && t.status !== 'Returned'">expected {{ t.expectedSettlementAt | date: 'MMM d' }}</div>
          <div class="muted" *ngIf="t.failureReason">{{ t.failureReason }}</div></td></ng-container>
      <tr mat-header-row *matHeaderRowDef="['when','to','amount','status']"></tr>
      <tr mat-row *matRowDef="let row; columns: ['when','to','amount','status']"></tr>
    </table>
  `
})
export class BankTransferPanelComponent implements OnInit, OnChanges {
    @Input({ required: true }) account!: Account;
    @Output() changed = new EventEmitter<void>();

    transfers: BankTransfer[] = [];
    fee: number | null = null;
    busy = false;

    form = this.fb.nonNullable.group({
        rail: ['Ach', Validators.required],
        beneficiaryName: ['', Validators.required],
        routingNumber: [''],
        beneficiaryAccountNumber: [''],
        swiftBic: [''],
        iban: [''],
        destinationCountry: [''],
        amount: [null as number | null, [Validators.required, Validators.min(0.01)]]
    });

    constructor(private fb: FormBuilder, private payments: PaymentsService, private snackBar: MatSnackBar) { }

    ngOnInit(): void {
        this.form.valueChanges.subscribe(() => this.updateFee());
        this.load();
    }

    ngOnChanges(): void {
        if (this.account) { this.load(); }
    }

    load(): void {
        if (!this.account) { return; }
        this.payments.getBankTransfers(this.account.id).subscribe(transfers => (this.transfers = transfers));
    }

    private updateFee(): void {
        const { rail, amount } = this.form.getRawValue();
        if (!amount || amount <= 0) {
            this.fee = null;
            return;
        }
        this.payments.getFee(rail, amount).subscribe(result => (this.fee = result.fee));
    }

    submit(): void {
        if (this.form.invalid || this.busy) { return; }
        const value = this.form.getRawValue();
        this.busy = true;

        this.payments.submitBankTransfer({
            accountId: this.account.id,
            rail: value.rail,
            amount: value.amount!,
            beneficiaryName: value.beneficiaryName,
            routingNumber: value.rail !== 'Swift' ? value.routingNumber : undefined,
            beneficiaryAccountNumber: value.beneficiaryAccountNumber || undefined,
            swiftBic: value.rail === 'Swift' ? value.swiftBic : undefined,
            iban: value.rail === 'Swift' ? value.iban || undefined : undefined,
            destinationCountry: value.rail === 'Swift' ? value.destinationCountry : undefined
        }).subscribe({
            next: () => {
                this.busy = false;
                this.form.patchValue({ beneficiaryName: '', routingNumber: '', beneficiaryAccountNumber: '', swiftBic: '', iban: '', destinationCountry: '', amount: null });
                this.snackBar.open('Transfer submitted', 'Close', { duration: 3000 });
                this.load();
                this.changed.emit();
            },
            error: error => {
                this.busy = false;
                this.snackBar.open(errorMessage(error), 'Close', { duration: 6000 });
            }
        });
    }
}

/** Payment history for an account, with refunds and disputes. */
@Component({
    selector: 'app-payment-history-panel',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [PANEL_STYLES],
    template: `
    <p class="muted" *ngIf="payments.length === 0">No payments on this account yet.</p>
    <table mat-table [dataSource]="payments" class="full" *ngIf="payments.length > 0">
      <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Date</th><td mat-cell *matCellDef="let p">{{ p.createdAt | date: 'short' }}</td></ng-container>
      <ng-container matColumnDef="what"><th mat-header-cell *matHeaderCellDef>Payment</th>
        <td mat-cell *matCellDef="let p">{{ p.kind === 'CardTopUp' ? 'Card top-up (' + p.provider + ')' : p.merchantName }}
          <div class="muted" *ngIf="p.mode">{{ p.mode }} gateway</div><div class="muted" *ngIf="p.errorMessage">{{ p.errorMessage }}</div></td></ng-container>
      <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th><td mat-cell *matCellDef="let p">{{ p.amount | currency: p.currency }}</td></ng-container>
      <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let p"><span class="badge" [class.good]="p.status === 'Completed'" [class.bad]="p.status === 'Failed' || p.status === 'Disputed'">{{ p.status }}</span></td></ng-container>
      <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let p">
          <button mat-button *ngIf="p.status === 'Completed'" (click)="refund(p)">Refund</button>
          <button mat-button color="warn" *ngIf="p.status === 'Completed' && p.kind === 'MerchantPayment'" (click)="dispute(p)">Dispute</button>
        </td></ng-container>
      <tr mat-header-row *matHeaderRowDef="['when','what','amount','status','actions']"></tr>
      <tr mat-row *matRowDef="let row; columns: ['when','what','amount','status','actions']"></tr>
    </table>
  `
})
export class PaymentHistoryPanelComponent implements OnInit, OnChanges {
    @Input({ required: true }) account!: Account;
    @Output() changed = new EventEmitter<void>();

    payments: PaymentRecord[] = [];

    constructor(private paymentsService: PaymentsService, private snackBar: MatSnackBar) { }

    ngOnInit(): void {
        this.load();
    }

    ngOnChanges(): void {
        if (this.account) { this.load(); }
    }

    load(): void {
        if (!this.account) { return; }
        this.paymentsService.getPayments(this.account.id).subscribe(payments => (this.payments = payments));
    }

    refund(payment: PaymentRecord): void {
        const what = payment.kind === 'CardTopUp' ? 'Take this top-up back out of the account and refund the card?' : 'Refund this payment in full?';
        if (!confirm(what)) { return; }

        this.paymentsService.refund(payment.transactionId).subscribe({
            next: () => {
                this.snackBar.open('Refunded', 'Close', { duration: 3000 });
                this.load();
                this.changed.emit();
            },
            error: error => this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 })
        });
    }

    dispute(payment: PaymentRecord): void {
        const reason = prompt(`Why are you disputing the payment to ${payment.merchantName}?`);
        if (!reason?.trim()) { return; }

        this.paymentsService.openChargeback(payment.transactionId, reason.trim()).subscribe({
            next: () => {
                this.snackBar.open('Dispute opened. The bank will review it.', 'Close', { duration: 4000 });
                this.load();
            },
            error: error => this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 })
        });
    }
}
