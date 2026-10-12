import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { User } from '../models/auth.model';
import { KycDocument, TwoFactorSetup } from '../models/api.models';
import { environment } from '../../environments/environment';

export interface UpdateProfileRequest {
    firstName: string;
    lastName: string;
    phone?: string;
    address?: string;
    city?: string;
    state?: string;
    country?: string;
    postalCode?: string;
}

@Injectable({ providedIn: 'root' })
export class ProfileService {
    private user = `${environment.apiUrl}/user`;
    private auth = `${environment.apiUrl}/auth`;

    constructor(private http: HttpClient) { }

    getProfile(): Observable<User> {
        return this.http.get<User>(`${this.user}/profile`);
    }

    updateProfile(request: UpdateProfileRequest): Observable<User> {
        return this.http.put<User>(`${this.user}/profile`, request);
    }

    changePassword(currentPassword: string, newPassword: string): Observable<{ message: string }> {
        return this.http.post<{ message: string }>(`${this.user}/change-password`, { currentPassword, newPassword });
    }

    closeProfile(password: string): Observable<{ message: string }> {
        return this.http.request<{ message: string }>('DELETE', `${this.user}`, { body: { password } });
    }

    // two-factor

    beginTwoFactor(): Observable<TwoFactorSetup> {
        return this.http.post<TwoFactorSetup>(`${this.auth}/2fa/setup`, {});
    }

    enableTwoFactor(code: string): Observable<{ message: string }> {
        return this.http.post<{ message: string }>(`${this.auth}/2fa/enable`, { code });
    }

    disableTwoFactor(password: string, code: string): Observable<{ message: string }> {
        return this.http.post<{ message: string }>(`${this.auth}/2fa/disable`, { password, code });
    }

    // KYC

    getKyc(): Observable<{ kycStatus: string; documents: KycDocument[] }> {
        return this.http.get<{ kycStatus: string; documents: KycDocument[] }>(`${this.user}/kyc/documents`);
    }

    uploadKycDocument(documentType: string, file: File): Observable<KycDocument> {
        const form = new FormData();
        form.append('documentType', documentType);
        form.append('file', file);
        return this.http.post<KycDocument>(`${this.user}/kyc/documents`, form);
    }

    static queryParams(values: Record<string, string | number | undefined>): HttpParams {
        let params = new HttpParams();
        for (const [key, value] of Object.entries(values)) {
            if (value !== undefined && value !== '') {
                params = params.set(key, String(value));
            }
        }
        return params;
    }
}
