import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
    AddPaymentMethodRequest,
    BankTransfer,
    BankTransferRequest,
    Chargeback,
    PaymentMethod,
    PaymentRecord,
    RecurringPayment
} from '../models/api.models';
import { PaymentResult } from '../models/account.model';
import { environment } from '../../environments/environment';

@Injectable({ providedIn: 'root' })
export class PaymentsService {
    private api = environment.apiUrl;

    constructor(private http: HttpClient) { }

    // payment methods

    getMethods(): Observable<PaymentMethod[]> {
        return this.http.get<PaymentMethod[]>(`${this.api}/payment-methods`);
    }

    addMethod(request: AddPaymentMethodRequest): Observable<PaymentMethod> {
        return this.http.post<PaymentMethod>(`${this.api}/payment-methods`, request);
    }

    makeDefault(id: string): Observable<void> {
        return this.http.post<void>(`${this.api}/payment-methods/${id}/default`, {});
    }

    removeMethod(id: string): Observable<void> {
        return this.http.delete<void>(`${this.api}/payment-methods/${id}`);
    }

    // card top-up, payment history, refunds, disputes

    topUp(accountId: string, paymentMethodId: string, amount: number): Observable<PaymentResult> {
        return this.http.post<PaymentResult>(`${this.api}/payments/top-up`, { accountId, paymentMethodId, amount });
    }

    getPayments(accountId: string): Observable<PaymentRecord[]> {
        return this.http.get<PaymentRecord[]>(`${this.api}/payments`, { params: new HttpParams().set('accountId', accountId) });
    }

    refund(transactionId: string, amount?: number): Observable<PaymentResult> {
        return this.http.post<PaymentResult>(`${this.api}/payments/${transactionId}/refund`, amount ? { amount } : {});
    }

    openChargeback(transactionId: string, reason: string): Observable<Chargeback> {
        return this.http.post<Chargeback>(`${this.api}/payments/${transactionId}/chargeback`, { reason });
    }

    getChargebacks(): Observable<Chargeback[]> {
        return this.http.get<Chargeback[]>(`${this.api}/payments/chargebacks`);
    }

    // recurring payments

    getRecurring(): Observable<RecurringPayment[]> {
        return this.http.get<RecurringPayment[]>(`${this.api}/recurring-payments`);
    }

    createRecurring(body: { accountId: string; merchantName: string; amount: number; frequency: string; startAt?: string }): Observable<RecurringPayment> {
        return this.http.post<RecurringPayment>(`${this.api}/recurring-payments`, body);
    }

    setRecurring(id: string, action: 'pause' | 'resume' | 'cancel'): Observable<void> {
        return action === 'cancel'
            ? this.http.delete<void>(`${this.api}/recurring-payments/${id}`)
            : this.http.post<void>(`${this.api}/recurring-payments/${id}/${action}`, {});
    }

    // ACH / wire / SWIFT

    getBankTransfers(accountId: string): Observable<BankTransfer[]> {
        return this.http.get<BankTransfer[]>(`${this.api}/bank-transfers`, { params: new HttpParams().set('accountId', accountId) });
    }

    submitBankTransfer(request: BankTransferRequest): Observable<BankTransfer> {
        return this.http.post<BankTransfer>(`${this.api}/bank-transfers`, request);
    }

    getFee(rail: string, amount: number): Observable<{ fee: number }> {
        return this.http.get<{ fee: number }>(`${this.api}/bank-transfers/fees`, {
            params: new HttpParams().set('rail', rail).set('amount', amount)
        });
    }
}
