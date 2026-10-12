import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { BehaviorSubject, Observable, tap } from 'rxjs';
import { AuthResponse, LoginRequest, RegisterRequest, User } from '../models/auth.model';
import { environment } from '../../environments/environment';

@Injectable({
    providedIn: 'root'
})
export class AuthService {
    private currentUserSubject: BehaviorSubject<User | null>;
    public currentUser: Observable<User | null>;
    private apiUrl = `${environment.apiUrl}/auth`;

    constructor(private http: HttpClient) {
        this.currentUserSubject = new BehaviorSubject<User | null>(this.readStoredUser());
        this.currentUser = this.currentUserSubject.asObservable();
    }

    public get currentUserValue(): User | null {
        return this.currentUserSubject.value;
    }

    login(credentials: LoginRequest): Observable<AuthResponse> {
        return this.http.post<AuthResponse>(`${this.apiUrl}/login`, credentials)
            .pipe(tap(response => this.storeSession(response)));
    }

    register(data: RegisterRequest): Observable<AuthResponse> {
        return this.http.post<AuthResponse>(`${this.apiUrl}/register`, data)
            .pipe(tap(response => this.storeSession(response)));
    }

    /** Exchanges the stored refresh token for a new access/refresh token pair. */
    refresh(): Observable<AuthResponse> {
        return this.http.post<AuthResponse>(`${this.apiUrl}/refresh`, { refreshToken: this.getRefreshToken() })
            .pipe(tap(response => this.storeSession(response)));
    }

    logout(): void {
        localStorage.removeItem('accessToken');
        localStorage.removeItem('refreshToken');
        localStorage.removeItem('currentUser');
        this.currentUserSubject.next(null);
    }

    isAuthenticated(): boolean {
        return !!localStorage.getItem('accessToken');
    }

    getToken(): string | null {
        return localStorage.getItem('accessToken');
    }

    getRefreshToken(): string | null {
        return localStorage.getItem('refreshToken');
    }

    private storeSession(response: AuthResponse): void {
        localStorage.setItem('accessToken', response.accessToken);
        localStorage.setItem('refreshToken', response.refreshToken);
        localStorage.setItem('currentUser', JSON.stringify(response.user));
        this.currentUserSubject.next(response.user);
    }

    private readStoredUser(): User | null {
        try {
            const stored = localStorage.getItem('currentUser');
            return stored ? JSON.parse(stored) as User : null;
        } catch {
            return null;
        }
    }
}
