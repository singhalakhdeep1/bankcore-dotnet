import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { Account, CreateAccountRequest } from '../models/account.model';
import { environment } from '../../environments/environment';

@Injectable({
    providedIn: 'root'
})
export class AccountService {
    private apiUrl = `${environment.apiUrl}/accounts`;

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
}
