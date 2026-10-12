import { Component, OnInit } from '@angular/core';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';
import { Router, ActivatedRoute } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../../services/auth.service';

@Component({
    selector: 'app-login',
    templateUrl: './login.component.html',
    styleUrls: ['./login.component.scss']
})
export class LoginComponent implements OnInit {
    loginForm: FormGroup;
    loading = false;
    hidePassword = true;
    requiresTwoFactor = false;
    returnUrl: string = '/dashboard';

    constructor(
        private formBuilder: FormBuilder,
        private authService: AuthService,
        private router: Router,
        private route: ActivatedRoute,
        private snackBar: MatSnackBar
    ) {
        this.loginForm = this.formBuilder.group({
            email: ['', [Validators.required, Validators.email]],
            password: ['', Validators.required],
            totpCode: ['']
        });
    }

    ngOnInit(): void {
        if (this.authService.isAuthenticated()) {
            this.router.navigate(['/dashboard']);
        }
        this.returnUrl = this.route.snapshot.queryParams['returnUrl'] || '/dashboard';
    }

    onSubmit(): void {
        if (this.loginForm.invalid) {
            return;
        }

        this.loading = true;
        this.authService.login({
            email: this.loginForm.value.email,
            password: this.loginForm.value.password,
            totpCode: this.requiresTwoFactor ? this.loginForm.value.totpCode : undefined
        }).subscribe({
            next: () => {
                this.router.navigate([this.returnUrl]);
            },
            error: (error) => {
                this.loading = false;

                // The password was right but this account has two-factor on: ask for the authenticator code.
                if (error.status === 401 && error.error?.requiresTwoFactor) {
                    this.requiresTwoFactor = true;
                    this.loginForm.get('totpCode')?.setValidators([Validators.required, Validators.pattern(/^\d{6}$/)]);
                    this.loginForm.get('totpCode')?.updateValueAndValidity();
                }

                const message = error.status === 423
                    ? error.error?.message || 'This account is locked.'
                    : error.error?.message || 'Login failed';
                this.snackBar.open(message, 'Close', { duration: 5000 });
            }
        });
    }
}
