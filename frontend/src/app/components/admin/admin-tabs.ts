import { Component, OnInit } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { PageEvent } from '@angular/material/paginator';
import { AdminApiService } from '../../services/admin-api.service';
import { AuthService } from '../../services/auth.service';
import {
    AdminUser,
    AuditEntry,
    BalanceSheetSection,
    BankTransfer,
    Chargeback,
    ComplianceAlert,
    LedgerPeriod,
    PaymentAnalytics,
    PendingDocument,
    Reconciliation,
    RegulatoryReport,
    SanctionedEntity,
    TrialBalance
} from '../../models/api.models';
import { PAGE_IMPORTS, errorMessage } from '../../shared/material';

const TAB_STYLES = `
  .tab { padding: 16px 0; }
  .row { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 12px; }
  .full { width: 100%; }
  .muted { color: #777; font-size: 13px; }
  .badge { padding: 2px 10px; border-radius: 10px; font-size: 12px; background: #eee; }
  .good { background: #e8f5e9; color: #2e7d32; }
  .bad { background: #ffebee; color: #c62828; }
  .warn { background: #fff8e1; color: #8d6e00; }
  .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin-bottom: 16px; }
  .stat { padding: 12px; border: 1px solid #e0e0e0; border-radius: 8px; }
  .stat b { display: block; font-size: 20px; }
  pre { background: #f5f5f5; padding: 12px; border-radius: 6px; max-height: 320px; overflow: auto; font-size: 12px; }
  h3 { margin: 24px 0 8px; }
`;

abstract class TabBase {
    constructor(protected api: AdminApiService, protected snackBar: MatSnackBar) { }

    protected ok(message: string): void {
        this.snackBar.open(message, 'Close', { duration: 3000 });
    }

    protected fail(error: unknown): void {
        this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 });
    }
}

// ---------------------------------------------------------------- users

@Component({
    selector: 'app-admin-users',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <div class="row">
        <mat-form-field appearance="outline"><mat-label>Search name or email</mat-label><input matInput [formControl]="search" (keyup.enter)="load(1)" /></mat-form-field>
        <button mat-stroked-button (click)="load(1)">Search</button>
      </div>
      <table mat-table [dataSource]="users" class="full">
        <ng-container matColumnDef="user"><th mat-header-cell *matHeaderCellDef>User</th>
          <td mat-cell *matCellDef="let u">{{ u.firstName }} {{ u.lastName }}<div class="muted">{{ u.email }}</div></td></ng-container>
        <ng-container matColumnDef="role"><th mat-header-cell *matHeaderCellDef>Role</th>
          <td mat-cell *matCellDef="let u">
            <mat-select [value]="u.role" (selectionChange)="setRole(u, $event.value)" [disabled]="!isAdmin">
              <mat-option *ngFor="let r of roles" [value]="r">{{ r }}</mat-option>
            </mat-select></td></ng-container>
        <ng-container matColumnDef="kyc"><th mat-header-cell *matHeaderCellDef>KYC</th>
          <td mat-cell *matCellDef="let u">
            <mat-select [value]="u.kycStatus" (selectionChange)="setKyc(u, $event.value)">
              <mat-option *ngFor="let k of kycStates" [value]="k">{{ k }}</mat-option>
            </mat-select></td></ng-container>
        <ng-container matColumnDef="state"><th mat-header-cell *matHeaderCellDef>State</th>
          <td mat-cell *matCellDef="let u">
            <span class="badge bad" *ngIf="u.isLocked">Locked</span>
            <span class="badge warn" *ngIf="u.lockoutEnd && !u.isLocked">Temporarily locked</span>
            <span class="badge good" *ngIf="!u.isLocked && !u.lockoutEnd">Active</span>
            <span class="badge" *ngIf="u.twoFactorEnabled">2FA</span>
            <div class="muted">{{ u.accountCount }} accounts</div></td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let u">
            <button mat-button *ngIf="!u.isLocked && !u.lockoutEnd" (click)="lock(u, true)">Lock</button>
            <button mat-button *ngIf="u.isLocked || u.lockoutEnd" (click)="lock(u, false)">Unlock</button>
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="cols"></tr>
        <tr mat-row *matRowDef="let row; columns: cols"></tr>
      </table>
      <mat-paginator [length]="total" [pageSize]="20" [pageIndex]="page - 1" [hidePageSize]="true" (page)="onPage($event)"></mat-paginator>
      <p class="muted" *ngIf="!isAdmin">Only administrators can change roles; managers can lock and unlock; staff can change KYC status.</p>
    </div>
  `
})
export class AdminUsersTabComponent extends TabBase implements OnInit {
    users: AdminUser[] = [];
    total = 0;
    page = 1;
    search = this.fb.nonNullable.control('');
    cols = ['user', 'role', 'kyc', 'state', 'actions'];
    roles = ['Customer', 'Staff', 'Manager', 'Admin'];
    kycStates = ['Pending', 'Verified', 'Rejected'];
    isAdmin = false;

    constructor(api: AdminApiService, snackBar: MatSnackBar, private fb: FormBuilder, auth: AuthService) {
        super(api, snackBar);
        this.isAdmin = auth.currentUserValue?.role === 'Admin';
    }

    ngOnInit(): void { this.load(1); }

    load(page: number): void {
        this.page = page;
        this.api.users(this.search.value, page).subscribe({
            next: r => { this.users = r.items; this.total = r.totalCount; },
            error: e => this.fail(e)
        });
    }

    onPage(event: PageEvent): void { this.load(event.pageIndex + 1); }

    setRole(user: AdminUser, role: string): void {
        this.api.setRole(user.id, role).subscribe({ next: () => { this.ok(`${user.email} is now ${role}`); this.load(this.page); }, error: e => { this.fail(e); this.load(this.page); } });
    }

    setKyc(user: AdminUser, status: string): void {
        this.api.setKyc(user.id, status).subscribe({ next: () => this.ok(`KYC set to ${status}`), error: e => { this.fail(e); this.load(this.page); } });
    }

    lock(user: AdminUser, locked: boolean): void {
        this.api.setLocked(user.id, locked).subscribe({ next: () => { this.ok(locked ? 'Locked' : 'Unlocked'); this.load(this.page); }, error: e => this.fail(e) });
    }
}

// ---------------------------------------------------------------- KYC queue

@Component({
    selector: 'app-admin-kyc',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <p class="muted" *ngIf="documents.length === 0">No documents are waiting for review.</p>
      <table mat-table [dataSource]="documents" class="full" *ngIf="documents.length > 0">
        <ng-container matColumnDef="user"><th mat-header-cell *matHeaderCellDef>Customer</th><td mat-cell *matCellDef="let d">{{ d.userEmail }}</td></ng-container>
        <ng-container matColumnDef="doc"><th mat-header-cell *matHeaderCellDef>Document</th><td mat-cell *matCellDef="let d">{{ d.documentType }}<div class="muted">{{ d.fileName }} · {{ d.sizeBytes / 1024 | number: '1.0-0' }} KB</div></td></ng-container>
        <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Uploaded</th><td mat-cell *matCellDef="let d">{{ d.uploadedAt | date: 'short' }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let d">
            <button mat-button (click)="view(d)">View</button>
            <button mat-button color="primary" (click)="review(d, true)">Approve</button>
            <button mat-button color="warn" (click)="review(d, false)">Reject</button>
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['user','doc','when','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['user','doc','when','actions']"></tr>
      </table>
    </div>
  `
})
export class AdminKycTabComponent extends TabBase implements OnInit {
    documents: PendingDocument[] = [];

    constructor(api: AdminApiService, snackBar: MatSnackBar) { super(api, snackBar); }

    ngOnInit(): void { this.load(); }

    load(): void {
        this.api.pendingDocuments().subscribe({ next: d => (this.documents = d), error: e => this.fail(e) });
    }

    view(document: PendingDocument): void {
        this.api.documentBlob(document.id).subscribe({
            next: blob => {
                const url = URL.createObjectURL(new Blob([blob], { type: document.contentType }));
                window.open(url, '_blank');
            },
            error: e => this.fail(e)
        });
    }

    review(document: PendingDocument, approve: boolean): void {
        const notes = approve ? undefined : prompt('Reason for rejecting this document?') ?? undefined;
        if (!approve && !notes) { return; }
        this.api.reviewDocument(document.id, approve, notes).subscribe({ next: () => { this.ok(approve ? 'Approved' : 'Rejected'); this.load(); }, error: e => this.fail(e) });
    }
}

// ---------------------------------------------------------------- compliance

@Component({
    selector: 'app-admin-compliance',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <h3>Alerts</h3>
      <div class="row">
        <mat-form-field appearance="outline" style="width: 180px">
          <mat-label>Status</mat-label>
          <mat-select [formControl]="statusFilter" (selectionChange)="loadAlerts()">
            <mat-option value="">All</mat-option>
            <mat-option *ngFor="let s of statuses" [value]="s">{{ s }}</mat-option>
          </mat-select>
        </mat-form-field>
      </div>
      <table mat-table [dataSource]="alerts" class="full">
        <ng-container matColumnDef="alert"><th mat-header-cell *matHeaderCellDef>Alert</th>
          <td mat-cell *matCellDef="let a"><b>{{ a.alertType }}</b> <span class="badge" [class.bad]="a.severity === 'High'" [class.warn]="a.severity === 'Medium'">{{ a.severity }}</span>
            <div class="muted">{{ a.description }}</div></td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th><td mat-cell *matCellDef="let a">{{ a.status }}<div class="muted" *ngIf="a.reviewNotes">{{ a.reviewNotes }}</div></td></ng-container>
        <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Raised</th><td mat-cell *matCellDef="let a">{{ a.createdAt | date: 'short' }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let a">
            <button mat-button (click)="review(a, 'UnderReview')">Review</button>
            <button mat-button (click)="review(a, 'Escalated')">Escalate</button>
            <button mat-button (click)="review(a, 'Dismissed')">Dismiss</button>
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['alert','status','when','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['alert','status','when','actions']"></tr>
      </table>
      <mat-paginator [length]="alertTotal" [pageSize]="20" [pageIndex]="alertPage - 1" [hidePageSize]="true" (page)="loadAlerts($event.pageIndex + 1)"></mat-paginator>

      <h3>Regulatory reports</h3>
      <form [formGroup]="reportForm" (ngSubmit)="generate()" class="row">
        <mat-form-field appearance="outline" style="width: 260px">
          <mat-label>Report</mat-label>
          <mat-select formControlName="type">
            <mat-option value="SuspiciousActivity">Suspicious activity (SAR)</mat-option>
            <mat-option value="CurrencyTransaction">Currency transactions (CTR)</mat-option>
            <mat-option value="MonthlyCompliance">Monthly compliance summary</mat-option>
          </mat-select>
        </mat-form-field>
        <mat-form-field appearance="outline"><mat-label>From</mat-label><input matInput type="date" formControlName="from" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>To</mat-label><input matInput type="date" formControlName="to" /></mat-form-field>
        <button mat-raised-button color="primary" type="submit" [disabled]="reportForm.invalid">Generate</button>
      </form>
      <table mat-table [dataSource]="reports" class="full" *ngIf="reports.length > 0">
        <ng-container matColumnDef="type"><th mat-header-cell *matHeaderCellDef>Report</th><td mat-cell *matCellDef="let r">{{ r.type }}</td></ng-container>
        <ng-container matColumnDef="period"><th mat-header-cell *matHeaderCellDef>Period</th><td mat-cell *matCellDef="let r">{{ r.periodStart | date: 'mediumDate' }} – {{ r.periodEnd | date: 'mediumDate' }}</td></ng-container>
        <ng-container matColumnDef="items"><th mat-header-cell *matHeaderCellDef>Items</th><td mat-cell *matCellDef="let r">{{ r.itemCount }}</td></ng-container>
        <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Generated</th><td mat-cell *matCellDef="let r">{{ r.generatedAt | date: 'short' }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th><td mat-cell *matCellDef="let r"><button mat-button (click)="open(r)">View</button></td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['type','period','items','when','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['type','period','items','when','actions']"></tr>
      </table>
      <pre *ngIf="openReport">{{ openReport | json }}</pre>

      <h3>Sanctions watch list</h3>
      <form [formGroup]="sanctionForm" (ngSubmit)="addSanction()" class="row">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Account number (optional)</mat-label><input matInput formControlName="accountNumber" /></mat-form-field>
        <button mat-stroked-button type="submit" [disabled]="sanctionForm.invalid || !isAdmin">Add entry</button>
        <mat-form-field appearance="outline"><mat-label>Screen a name</mat-label><input matInput [formControl]="screenName" (keyup.enter)="screen()" /></mat-form-field>
        <button mat-stroked-button type="button" (click)="screen()">Screen</button>
      </form>
      <p *ngIf="screenResult" class="muted">{{ screenResult }}</p>
      <table mat-table [dataSource]="sanctions" class="full">
        <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Name</th><td mat-cell *matCellDef="let s">{{ s.name }}<div class="muted">{{ s.listName }} {{ s.country }} {{ s.accountNumber }}</div></td></ng-container>
        <ng-container matColumnDef="state"><th mat-header-cell *matHeaderCellDef>State</th><td mat-cell *matCellDef="let s">{{ s.isActive ? 'Active' : 'Removed' }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th><td mat-cell *matCellDef="let s"><button mat-button color="warn" *ngIf="s.isActive && isAdmin" (click)="removeSanction(s)">Remove</button></td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['name','state','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['name','state','actions']"></tr>
      </table>
    </div>
  `
})
export class AdminComplianceTabComponent extends TabBase implements OnInit {
    statuses = ['Open', 'UnderReview', 'Escalated', 'Dismissed'];
    alerts: ComplianceAlert[] = [];
    alertTotal = 0;
    alertPage = 1;
    reports: RegulatoryReport[] = [];
    openReport: unknown = null;
    sanctions: SanctionedEntity[] = [];
    screenResult = '';
    isAdmin = false;

    statusFilter = this.fb.nonNullable.control('');
    screenName = this.fb.nonNullable.control('');

    reportForm = this.fb.nonNullable.group({
        type: ['SuspiciousActivity', Validators.required],
        from: [new Date(Date.now() - 30 * 86400000).toISOString().slice(0, 10), Validators.required],
        to: [new Date().toISOString().slice(0, 10), Validators.required]
    });
    sanctionForm = this.fb.nonNullable.group({ name: ['', Validators.required], accountNumber: [''] });

    constructor(api: AdminApiService, snackBar: MatSnackBar, private fb: FormBuilder, auth: AuthService) {
        super(api, snackBar);
        this.isAdmin = auth.currentUserValue?.role === 'Admin';
    }

    ngOnInit(): void {
        this.loadAlerts();
        this.api.reports().subscribe({ next: r => (this.reports = r), error: e => this.fail(e) });
        this.loadSanctions();
    }

    loadAlerts(page = 1): void {
        this.alertPage = page;
        this.api.alerts(this.statusFilter.value || undefined, page).subscribe({
            next: r => { this.alerts = r.items; this.alertTotal = r.totalCount; },
            error: e => this.fail(e)
        });
    }

    review(alert: ComplianceAlert, status: string): void {
        const notes = prompt(`Note for "${status}" (optional)`) ?? '';
        this.api.reviewAlert(alert.id, status, notes).subscribe({ next: () => { this.ok('Alert updated'); this.loadAlerts(this.alertPage); }, error: e => this.fail(e) });
    }

    generate(): void {
        const { type, from, to } = this.reportForm.getRawValue();
        this.api.generateReport(type, from, to).subscribe({
            next: report => {
                this.ok(`Report generated (${report.itemCount} items)`);
                this.openReport = report.payload;
                this.api.reports().subscribe(r => (this.reports = r));
            },
            error: e => this.fail(e)
        });
    }

    open(report: RegulatoryReport): void {
        this.api.report(report.id).subscribe({ next: r => (this.openReport = r.payload), error: e => this.fail(e) });
    }

    loadSanctions(): void {
        this.api.sanctions().subscribe({ next: s => (this.sanctions = s), error: e => this.fail(e) });
    }

    addSanction(): void {
        const { name, accountNumber } = this.sanctionForm.getRawValue();
        this.api.addSanction(name, accountNumber || undefined).subscribe({
            next: () => { this.ok('Added'); this.sanctionForm.reset({ name: '', accountNumber: '' }); this.loadSanctions(); },
            error: e => this.fail(e)
        });
    }

    removeSanction(entry: SanctionedEntity): void {
        this.api.removeSanction(entry.id).subscribe({ next: () => { this.ok('Removed'); this.loadSanctions(); }, error: e => this.fail(e) });
    }

    screen(): void {
        if (!this.screenName.value.trim()) { return; }
        this.api.screen(this.screenName.value).subscribe({
            next: r => (this.screenResult = r.match ? `MATCH: ${r.entry?.name} (${r.entry?.listName})` : 'No match.'),
            error: e => this.fail(e)
        });
    }
}

// ---------------------------------------------------------------- ledger

@Component({
    selector: 'app-admin-ledger',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <div class="row">
        <button mat-stroked-button (click)="load()">Refresh</button>
        <button mat-stroked-button (click)="reconcile()">Reconcile every account</button>
        <span class="badge" *ngIf="trial" [class.good]="trial.balanced" [class.bad]="!trial.balanced">Trial balance {{ trial.balanced ? 'balances' : 'DOES NOT BALANCE' }}</span>
        <span class="badge" *ngIf="reconciliation" [class.good]="reconciliation.clean" [class.bad]="!reconciliation.clean">
          {{ reconciliation.clean ? 'All ' + reconciliation.accountsChecked + ' accounts match the ledger' : reconciliation.mismatches.length + ' accounts do not match' }}</span>
      </div>

      <h3>Trial balance</h3>
      <table mat-table [dataSource]="trial?.lines ?? []" class="full">
        <ng-container matColumnDef="account"><th mat-header-cell *matHeaderCellDef>GL account</th><td mat-cell *matCellDef="let l">{{ l.account }} <span class="muted">{{ l.currency }}</span></td></ng-container>
        <ng-container matColumnDef="debits"><th mat-header-cell *matHeaderCellDef>Debits</th><td mat-cell *matCellDef="let l">{{ l.debits | number: '1.2-2' }}</td></ng-container>
        <ng-container matColumnDef="credits"><th mat-header-cell *matHeaderCellDef>Credits</th><td mat-cell *matCellDef="let l">{{ l.credits | number: '1.2-2' }}</td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['account','debits','credits']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['account','debits','credits']"></tr>
      </table>
      <p class="muted" *ngFor="let t of trial?.totals">Total {{ t.currency }}: debits {{ t.debits | number: '1.2-2' }} = credits {{ t.credits | number: '1.2-2' }}</p>

      <h3>Balance sheet</h3>
      <div class="stats" *ngFor="let s of sheet">
        <div class="stat"><span class="muted">{{ s.currency }} assets</span><b>{{ s.totalAssets | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Liabilities</span><b>{{ s.totalLiabilities | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Equity</span><b>{{ s.retainedEarnings | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Assets = L + E</span><b>{{ s.balanced ? 'Yes' : 'NO' }}</b></div>
      </div>

      <h3>Accounting periods</h3>
      <form [formGroup]="periodForm" (ngSubmit)="close()" class="row">
        <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput formControlName="name" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>Start</mat-label><input matInput type="date" formControlName="start" /></mat-form-field>
        <mat-form-field appearance="outline"><mat-label>End</mat-label><input matInput type="date" formControlName="end" /></mat-form-field>
        <button mat-raised-button color="primary" type="submit" [disabled]="periodForm.invalid">Close period</button>
      </form>
      <table mat-table [dataSource]="periods" class="full" *ngIf="periods.length > 0">
        <ng-container matColumnDef="name"><th mat-header-cell *matHeaderCellDef>Period</th><td mat-cell *matCellDef="let p">{{ p.name }}</td></ng-container>
        <ng-container matColumnDef="range"><th mat-header-cell *matHeaderCellDef>Dates</th><td mat-cell *matCellDef="let p">{{ p.startDate | date: 'mediumDate' }} – {{ p.endDate | date: 'mediumDate' }}</td></ng-container>
        <ng-container matColumnDef="totals"><th mat-header-cell *matHeaderCellDef>Debits / credits</th><td mat-cell *matCellDef="let p">{{ p.totalDebits | number: '1.2-2' }} / {{ p.totalCredits | number: '1.2-2' }}</td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th><td mat-cell *matCellDef="let p"><span class="badge good">{{ p.status }}</span></td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['name','range','totals','status']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['name','range','totals','status']"></tr>
      </table>
    </div>
  `
})
export class AdminLedgerTabComponent extends TabBase implements OnInit {
    trial: TrialBalance | null = null;
    sheet: BalanceSheetSection[] = [];
    periods: LedgerPeriod[] = [];
    reconciliation: Reconciliation | null = null;

    periodForm = this.fb.nonNullable.group({
        name: ['', Validators.required],
        start: ['', Validators.required],
        end: ['', Validators.required]
    });

    constructor(api: AdminApiService, snackBar: MatSnackBar, private fb: FormBuilder) { super(api, snackBar); }

    ngOnInit(): void { this.load(); }

    load(): void {
        this.api.trialBalance().subscribe({ next: r => (this.trial = r.trial), error: e => this.fail(e) });
        this.api.balanceSheet().subscribe({ next: r => (this.sheet = r.sections), error: e => this.fail(e) });
        this.api.periods().subscribe({ next: p => (this.periods = p), error: e => this.fail(e) });
    }

    reconcile(): void {
        this.api.reconcile().subscribe({ next: r => (this.reconciliation = r), error: e => this.fail(e) });
    }

    close(): void {
        const { name, start, end } = this.periodForm.getRawValue();
        if (!confirm(`Close "${name}"? Closed periods cannot be reopened.`)) { return; }
        this.api.closePeriod(name, start, end).subscribe({
            next: () => { this.ok('Period closed'); this.periodForm.reset({ name: '', start: '', end: '' }); this.load(); },
            error: e => this.fail(e)
        });
    }
}

// ---------------------------------------------------------------- payments

@Component({
    selector: 'app-admin-payments',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <div class="stats" *ngIf="analytics">
        <div class="stat"><span class="muted">Payments (30 days)</span><b>{{ analytics.totalPayments }}</b></div>
        <div class="stat"><span class="muted">Success rate</span><b>{{ analytics.successRate * 100 | number: '1.0-1' }}%</b></div>
        <div class="stat"><span class="muted">Completed volume</span><b>{{ analytics.completedVolume | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Refunded</span><b>{{ analytics.refundedVolume | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Gateway fees</span><b>{{ analytics.gatewayFees | number: '1.2-2' }}</b></div>
        <div class="stat"><span class="muted">Chargebacks</span><b>{{ analytics.chargebacksOpened }}</b></div>
      </div>
      <div class="row">
        <button mat-stroked-button (click)="checkPayments()">Reconcile payments with the ledger</button>
        <span class="badge" *ngIf="paymentRecon" [class.good]="paymentRecon.clean" [class.bad]="!paymentRecon.clean">
          {{ paymentRecon.clean ? 'Clean (' + paymentRecon.paymentsChecked + ' checked)' : paymentRecon.issues.length + ' issues' }}</span>
      </div>
      <p class="muted" *ngFor="let i of paymentRecon?.issues">{{ i.paymentReference }}: {{ i.issue }}</p>

      <h3>Chargebacks</h3>
      <p class="muted" *ngIf="chargebacks.length === 0">No disputes.</p>
      <table mat-table [dataSource]="chargebacks" class="full" *ngIf="chargebacks.length > 0">
        <ng-container matColumnDef="ref"><th mat-header-cell *matHeaderCellDef>Payment</th><td mat-cell *matCellDef="let c">{{ c.paymentReference }}<div class="muted">{{ c.reason }}</div></td></ng-container>
        <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th><td mat-cell *matCellDef="let c">{{ c.amount | number: '1.2-2' }}</td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th><td mat-cell *matCellDef="let c">{{ c.status }}</td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let c">
            <button mat-button *ngIf="c.status === 'Opened'" (click)="startReview(c)">Start review</button>
            <button mat-button color="primary" *ngIf="c.status === 'Opened' || c.status === 'UnderReview'" (click)="resolve(c, 'CustomerWon')">Customer wins</button>
            <button mat-button color="warn" *ngIf="c.status === 'Opened' || c.status === 'UnderReview'" (click)="resolve(c, 'MerchantWon')">Reject dispute</button>
          </td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['ref','amount','status','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['ref','amount','status','actions']"></tr>
      </table>

      <h3>Bank transfers</h3>
      <table mat-table [dataSource]="transfers" class="full" *ngIf="transfers.length > 0">
        <ng-container matColumnDef="ref"><th mat-header-cell *matHeaderCellDef>Reference</th><td mat-cell *matCellDef="let t">{{ t.reference }}<div class="muted">{{ t.beneficiaryName }}</div></td></ng-container>
        <ng-container matColumnDef="amount"><th mat-header-cell *matHeaderCellDef>Amount</th><td mat-cell *matCellDef="let t">{{ t.amount | number: '1.2-2' }} {{ t.currency }}</td></ng-container>
        <ng-container matColumnDef="status"><th mat-header-cell *matHeaderCellDef>Status</th><td mat-cell *matCellDef="let t"><span class="badge" [class.good]="t.status === 'Settled'" [class.bad]="t.status === 'Returned'">{{ t.status }}</span></td></ng-container>
        <ng-container matColumnDef="actions"><th mat-header-cell *matHeaderCellDef></th>
          <td mat-cell *matCellDef="let t"><button mat-button color="warn" *ngIf="t.status === 'Processing' || t.status === 'Settled' || t.status === 'Submitted'" (click)="returnTransfer(t)">Mark returned</button></td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['ref','amount','status','actions']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['ref','amount','status','actions']"></tr>
      </table>
      <p class="muted" *ngIf="transfers.length === 0">No bank transfers.</p>
    </div>
  `
})
export class AdminPaymentsTabComponent extends TabBase implements OnInit {
    analytics: PaymentAnalytics | null = null;
    paymentRecon: { paymentsChecked: number; clean: boolean; issues: { paymentReference: string; issue: string }[] } | null = null;
    chargebacks: Chargeback[] = [];
    transfers: BankTransfer[] = [];

    constructor(api: AdminApiService, snackBar: MatSnackBar) { super(api, snackBar); }

    ngOnInit(): void { this.load(); }

    load(): void {
        this.api.analytics().subscribe({ next: a => (this.analytics = a), error: e => this.fail(e) });
        this.api.chargebacks().subscribe({ next: c => (this.chargebacks = c), error: e => this.fail(e) });
        this.api.bankTransfers().subscribe({ next: t => (this.transfers = t), error: e => this.fail(e) });
    }

    checkPayments(): void {
        this.api.paymentReconciliation().subscribe({ next: r => (this.paymentRecon = r), error: e => this.fail(e) });
    }

    startReview(chargeback: Chargeback): void {
        this.api.reviewChargeback(chargeback.id).subscribe({ next: () => this.load(), error: e => this.fail(e) });
    }

    resolve(chargeback: Chargeback, outcome: 'CustomerWon' | 'MerchantWon'): void {
        const notes = prompt('Resolution notes') ?? '';
        this.api.resolveChargeback(chargeback.id, outcome, notes).subscribe({ next: () => { this.ok('Resolved'); this.load(); }, error: e => this.fail(e) });
    }

    returnTransfer(transfer: BankTransfer): void {
        const reason = prompt('Return reason (for example "Account closed")');
        if (!reason) { return; }
        this.api.returnTransfer(transfer.id, reason).subscribe({ next: () => { this.ok('Marked as returned'); this.load(); }, error: e => this.fail(e) });
    }
}

// ---------------------------------------------------------------- system

@Component({
    selector: 'app-admin-system',
    standalone: true,
    imports: PAGE_IMPORTS,
    styles: [TAB_STYLES],
    template: `
    <div class="tab">
      <h3>Database integrations</h3>
      <div class="stats">
        <div class="stat" *ngFor="let d of databases | keyvalue"><span class="muted">{{ d.key }}</span>
          <b [class]="d.value === 'ok' ? '' : 'muted'">{{ d.value }}</b></div>
      </div>
      <h3>Audit log</h3>
      <table mat-table [dataSource]="audit" class="full">
        <ng-container matColumnDef="when"><th mat-header-cell *matHeaderCellDef>Time</th><td mat-cell *matCellDef="let a">{{ a.timestamp | date: 'MMM d, HH:mm:ss' }}</td></ng-container>
        <ng-container matColumnDef="action"><th mat-header-cell *matHeaderCellDef>Action</th><td mat-cell *matCellDef="let a">{{ a.action }}<div class="muted">{{ a.details }}</div></td></ng-container>
        <ng-container matColumnDef="who"><th mat-header-cell *matHeaderCellDef>User / IP</th><td mat-cell *matCellDef="let a"><span class="muted">{{ a.userId | slice: 0 : 8 }} {{ a.ipAddress }}</span></td></ng-container>
        <tr mat-header-row *matHeaderRowDef="['when','action','who']"></tr>
        <tr mat-row *matRowDef="let row; columns: ['when','action','who']"></tr>
      </table>
      <mat-paginator [length]="total" [pageSize]="50" [pageIndex]="page - 1" [hidePageSize]="true" (page)="load($event.pageIndex + 1)"></mat-paginator>
    </div>
  `
})
export class AdminSystemTabComponent extends TabBase implements OnInit {
    databases: Record<string, string> = {};
    audit: AuditEntry[] = [];
    total = 0;
    page = 1;

    constructor(api: AdminApiService, snackBar: MatSnackBar) { super(api, snackBar); }

    ngOnInit(): void {
        this.api.databases().subscribe({ next: d => (this.databases = d), error: e => this.fail(e) });
        this.load(1);
    }

    load(page: number): void {
        this.page = page;
        this.api.audit(page).subscribe({ next: r => { this.audit = r.items; this.total = r.totalCount; }, error: e => this.fail(e) });
    }
}

// ---------------------------------------------------------------- shell

@Component({
    selector: 'app-admin',
    standalone: true,
    imports: [
        ...PAGE_IMPORTS,
        AdminUsersTabComponent,
        AdminKycTabComponent,
        AdminComplianceTabComponent,
        AdminLedgerTabComponent,
        AdminPaymentsTabComponent,
        AdminSystemTabComponent
    ],
    styles: ['.page { padding: 24px; max-width: 1200px; margin: 0 auto; }'],
    template: `
    <div class="page">
      <h1>Back office</h1>
      <mat-tab-group animationDuration="0ms">
        <mat-tab label="Users"><ng-template matTabContent><app-admin-users></app-admin-users></ng-template></mat-tab>
        <mat-tab label="KYC queue"><ng-template matTabContent><app-admin-kyc></app-admin-kyc></ng-template></mat-tab>
        <mat-tab label="Compliance"><ng-template matTabContent><app-admin-compliance></app-admin-compliance></ng-template></mat-tab>
        <mat-tab label="Ledger"><ng-template matTabContent><app-admin-ledger></app-admin-ledger></ng-template></mat-tab>
        <mat-tab label="Payments"><ng-template matTabContent><app-admin-payments></app-admin-payments></ng-template></mat-tab>
        <mat-tab label="System"><ng-template matTabContent><app-admin-system></app-admin-system></ng-template></mat-tab>
      </mat-tab-group>
    </div>
  `
})
export class AdminComponent { }
