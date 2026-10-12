import { Injectable } from '@angular/core';
import { HttpRequest, HttpHandler, HttpEvent, HttpInterceptor, HttpErrorResponse } from '@angular/common/http';
import { BehaviorSubject, Observable, throwError } from 'rxjs';
import { catchError, filter, switchMap, take } from 'rxjs/operators';
import { AuthService } from '../services/auth.service';
import { Router } from '@angular/router';

/**
 * Adds the bearer token to API calls. On a 401 it tries once to get a new access token with the
 * refresh token and replays the request; concurrent 401s share a single refresh call.
 */
@Injectable()
export class AuthInterceptor implements HttpInterceptor {
    private refreshing = false;
    /** null = refresh in progress, '' = refresh failed, otherwise the new access token. */
    private refreshResult = new BehaviorSubject<string | null>(null);

    constructor(
        private authService: AuthService,
        private router: Router
    ) { }

    intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
        const isAuthCall = request.url.includes('/auth/');
        const outgoing = isAuthCall ? request : this.withToken(request);

        return next.handle(outgoing).pipe(
            catchError((error: HttpErrorResponse) => {
                if (error.status === 401 && !isAuthCall) {
                    return this.handleUnauthorized(request, next, error);
                }
                return throwError(() => error);
            })
        );
    }

    private handleUnauthorized(
        request: HttpRequest<unknown>,
        next: HttpHandler,
        original: HttpErrorResponse
    ): Observable<HttpEvent<unknown>> {
        if (!this.authService.getRefreshToken()) {
            this.endSession();
            return throwError(() => original);
        }

        if (!this.refreshing) {
            this.refreshing = true;
            this.refreshResult.next(null);

            return this.authService.refresh().pipe(
                switchMap(response => {
                    this.refreshing = false;
                    this.refreshResult.next(response.accessToken);
                    return next.handle(this.withToken(request));
                }),
                catchError(refreshError => {
                    this.refreshing = false;
                    this.refreshResult.next('');
                    this.endSession();
                    return throwError(() => refreshError);
                })
            );
        }

        // Another request is already refreshing: wait for its outcome, then replay.
        return this.refreshResult.pipe(
            filter((token): token is string => token !== null),
            take(1),
            switchMap(token => token ? next.handle(this.withToken(request)) : throwError(() => original))
        );
    }

    private withToken(request: HttpRequest<unknown>): HttpRequest<unknown> {
        const token = this.authService.getToken();
        return token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;
    }

    private endSession(): void {
        this.authService.logout();
        this.router.navigate(['/login']);
    }
}
