import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import {
    Account,
    CreateAccountRequest,
    LedgerResponse,
    PagedResult,
    PaymentRequest,
    PaymentResult,
    Transaction,
    TransferRequest,
    TransferResult
} from '../models/account.model';
import { environment } from '../../environments/environment';

@Injectable({
    providedIn: 'root'
})
export class AccountService {
    private apiUrl = `${environment.apiUrl}/accounts`;
    private paymentsUrl = `${environment.apiUrl}/payments`;

    constructor(private http: HttpClient) { }

    getAccounts(): Observable<Account[]> {
        return this.http.get<Account[]>(this.apiUrl);
    }

    getAccount(id: string): Observable<Account> {
        return this.http.get<Account>(`${this.apiUrl}/${id}`);
    }

    createAccount(data: CreateAccountRequest): Observable<Account> {
        return this.http.post<Account>(this.apiUrl, data);
    }

    getTransactions(id: string, page = 1, pageSize = 10): Observable<PagedResult<Transaction>> {
        const params = new HttpParams().set('page', page).set('pageSize', pageSize);
        return this.http.get<PagedResult<Transaction>>(`${this.apiUrl}/${id}/transactions`, { params });
    }

    getLedger(id: string): Observable<LedgerResponse> {
        return this.http.get<LedgerResponse>(`${this.apiUrl}/${id}/ledger`);
    }

    deposit(id: string, amount: number, description?: string): Observable<Transaction> {
        return this.http.post<Transaction>(`${this.apiUrl}/${id}/deposit`, { amount, description });
    }

    withdraw(id: string, amount: number, description?: string): Observable<Transaction> {
        return this.http.post<Transaction>(`${this.apiUrl}/${id}/withdraw`, { amount, description });
    }

    transfer(request: TransferRequest): Observable<TransferResult> {
        return this.http.post<TransferResult>(`${this.apiUrl}/transfer`, request);
    }

    pay(request: PaymentRequest): Observable<PaymentResult> {
        return this.http.post<PaymentResult>(this.paymentsUrl, request);
    }
}
