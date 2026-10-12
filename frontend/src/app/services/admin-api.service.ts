import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
    AdminUser,
    AuditEntry,
    BalanceSheetSection,
    Chargeback,
    ComplianceAlert,
    LedgerPeriod,
    PageResult,
    PaymentAnalytics,
    PendingDocument,
    Reconciliation,
    RegulatoryReport,
    SanctionedEntity,
    TrialBalance,
    BankTransfer
} from '../models/api.models';
import { ProfileService } from './profile.service';
import { environment } from '../../environments/environment';

/** Back-office endpoints. They need a Staff, Manager or Admin role; the API enforces it. */
@Injectable({ providedIn: 'root' })
export class AdminApiService {
    private admin = `${environment.apiUrl}/admin`;

    constructor(private http: HttpClient) { }

    // users and KYC

    users(search: string, page = 1): Observable<PageResult<AdminUser>> {
        return this.http.get<PageResult<AdminUser>>(`${this.admin}/users`, { params: ProfileService.queryParams({ search, page, pageSize: 20 }) });
    }

    setRole(userId: string, role: string): Observable<void> {
        return this.http.put<void>(`${this.admin}/users/${userId}/role`, { role });
    }

    setLocked(userId: string, locked: boolean): Observable<void> {
        return this.http.post<void>(`${this.admin}/users/${userId}/${locked ? 'lock' : 'unlock'}`, {});
    }

    setKyc(userId: string, status: string, notes?: string): Observable<void> {
        return this.http.put<void>(`${this.admin}/users/${userId}/kyc`, { status, notes });
    }

    pendingDocuments(): Observable<PendingDocument[]> {
        return this.http.get<PendingDocument[]>(`${this.admin}/users/kyc/pending`);
    }

    documentBlob(id: string): Observable<Blob> {
        return this.http.get(`${this.admin}/users/kyc/documents/${id}/file`, { responseType: 'blob' });
    }

    reviewDocument(id: string, approve: boolean, notes?: string): Observable<void> {
        return this.http.post<void>(`${this.admin}/users/kyc/documents/${id}/review`, { approve, notes });
    }

    // compliance

    alerts(status?: string, page = 1): Observable<PageResult<ComplianceAlert>> {
        return this.http.get<PageResult<ComplianceAlert>>(`${this.admin}/compliance/alerts`, {
            params: ProfileService.queryParams({ status, page, pageSize: 20 })
        });
    }

    reviewAlert(id: string, status: string, notes: string): Observable<ComplianceAlert> {
        return this.http.put<ComplianceAlert>(`${this.admin}/compliance/alerts/${id}`, { status, notes });
    }

    sanctions(): Observable<SanctionedEntity[]> {
        return this.http.get<SanctionedEntity[]>(`${this.admin}/compliance/sanctions`);
    }

    addSanction(name: string, accountNumber?: string, country?: string): Observable<SanctionedEntity> {
        return this.http.post<SanctionedEntity>(`${this.admin}/compliance/sanctions`, { name, accountNumber, country });
    }

    removeSanction(id: string): Observable<void> {
        return this.http.delete<void>(`${this.admin}/compliance/sanctions/${id}`);
    }

    screen(name: string): Observable<{ match: boolean; entry?: SanctionedEntity }> {
        return this.http.post<{ match: boolean; entry?: SanctionedEntity }>(`${this.admin}/compliance/sanctions/screen`, { name });
    }

    reports(): Observable<RegulatoryReport[]> {
        return this.http.get<RegulatoryReport[]>(`${this.admin}/compliance/reports`);
    }

    report(id: string): Observable<RegulatoryReport> {
        return this.http.get<RegulatoryReport>(`${this.admin}/compliance/reports/${id}`);
    }

    generateReport(type: string, from: string, to: string): Observable<RegulatoryReport> {
        return this.http.post<RegulatoryReport>(`${this.admin}/compliance/reports`, { type, from, to });
    }

    // ledger

    trialBalance(): Observable<{ trial: TrialBalance }> {
        return this.http.get<{ trial: TrialBalance }>(`${this.admin}/ledger/trial-balance`);
    }

    balanceSheet(): Observable<{ asOf: string; sections: BalanceSheetSection[] }> {
        return this.http.get<{ asOf: string; sections: BalanceSheetSection[] }>(`${this.admin}/ledger/balance-sheet`);
    }

    reconcile(): Observable<Reconciliation> {
        return this.http.get<Reconciliation>(`${this.admin}/ledger/reconcile`);
    }

    periods(): Observable<LedgerPeriod[]> {
        return this.http.get<LedgerPeriod[]>(`${this.admin}/ledger/periods`);
    }

    closePeriod(name: string, start: string, end: string): Observable<LedgerPeriod> {
        return this.http.post<LedgerPeriod>(`${this.admin}/ledger/periods/close`, { name, start, end });
    }

    // payments

    analytics(): Observable<PaymentAnalytics> {
        return this.http.get<PaymentAnalytics>(`${this.admin}/payments/analytics`);
    }

    paymentReconciliation(): Observable<{ paymentsChecked: number; clean: boolean; issues: { paymentReference: string; issue: string }[] }> {
        return this.http.get<{ paymentsChecked: number; clean: boolean; issues: { paymentReference: string; issue: string }[] }>(`${this.admin}/payments/reconciliation`);
    }

    chargebacks(status?: string): Observable<Chargeback[]> {
        return this.http.get<Chargeback[]>(`${this.admin}/payments/chargebacks`, { params: ProfileService.queryParams({ status }) });
    }

    reviewChargeback(id: string): Observable<void> {
        return this.http.post<void>(`${this.admin}/payments/chargebacks/${id}/review`, {});
    }

    resolveChargeback(id: string, outcome: 'CustomerWon' | 'MerchantWon', notes: string): Observable<void> {
        return this.http.post<void>(`${this.admin}/payments/chargebacks/${id}/resolve`, { outcome, notes });
    }

    bankTransfers(status?: string): Observable<BankTransfer[]> {
        return this.http.get<BankTransfer[]>(`${this.admin}/payments/bank-transfers`, { params: ProfileService.queryParams({ status }) });
    }

    returnTransfer(id: string, reason: string): Observable<void> {
        return this.http.post<void>(`${this.admin}/payments/bank-transfers/${id}/return`, { reason });
    }

    // system

    audit(page = 1): Observable<PageResult<AuditEntry>> {
        return this.http.get<PageResult<AuditEntry>>(`${this.admin}/system/audit`, { params: ProfileService.queryParams({ page, pageSize: 50 }) });
    }

    databases(): Observable<Record<string, string>> {
        return this.http.get<Record<string, string>>(`${this.admin}/system/databases`);
    }
}
