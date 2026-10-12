import { Component, OnInit } from '@angular/core';
import { FormBuilder, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { AuthService } from '../../services/auth.service';
import { ProfileService } from '../../services/profile.service';
import { KycDocument, TwoFactorSetup } from '../../models/api.models';
import { User } from '../../models/auth.model';
import { PAGE_IMPORTS, errorMessage } from '../../shared/material';

@Component({
    selector: 'app-profile',
    standalone: true,
    imports: PAGE_IMPORTS,
    templateUrl: './profile.component.html',
    styleUrls: ['./profile.component.scss']
})
export class ProfileComponent implements OnInit {
    user: User | null = null;
    kycStatus = '';
    documents: KycDocument[] = [];
    documentTypes = ['Passport', 'NationalId', 'DrivingLicense', 'ProofOfAddress'];

    setup: TwoFactorSetup | null = null;

    profileForm = this.fb.nonNullable.group({
        firstName: ['', Validators.required],
        lastName: ['', Validators.required],
        phone: [''],
        address: [''],
        city: [''],
        state: [''],
        country: [''],
        postalCode: ['']
    });

    passwordForm = this.fb.nonNullable.group({
        currentPassword: ['', Validators.required],
        newPassword: ['', [Validators.required, Validators.minLength(10)]]
    });

    twoFactorForm = this.fb.nonNullable.group({ code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]] });
    disableForm = this.fb.nonNullable.group({
        password: ['', Validators.required],
        code: ['', [Validators.required, Validators.pattern(/^\d{6}$/)]]
    });
    kycForm = this.fb.nonNullable.group({ documentType: ['Passport', Validators.required] });
    closeForm = this.fb.nonNullable.group({ password: ['', Validators.required] });

    selectedFile: File | null = null;
    busy = false;

    constructor(
        private fb: FormBuilder,
        private profile: ProfileService,
        private auth: AuthService,
        private router: Router,
        private snackBar: MatSnackBar
    ) { }

    ngOnInit(): void {
        this.profile.getProfile().subscribe(user => this.applyUser(user));
        this.loadKyc();
    }

    private applyUser(user: User): void {
        this.user = user;
        this.profileForm.patchValue({
            firstName: user.firstName,
            lastName: user.lastName,
            phone: user.phone ?? '',
            address: user.address ?? '',
            city: user.city ?? '',
            state: user.state ?? '',
            country: user.country ?? '',
            postalCode: user.postalCode ?? ''
        });
    }

    private loadKyc(): void {
        this.profile.getKyc().subscribe(result => {
            this.kycStatus = result.kycStatus;
            this.documents = result.documents;
        });
    }

    saveProfile(): void {
        if (this.profileForm.invalid) { return; }
        this.run(this.profile.updateProfile(this.profileForm.getRawValue()), 'Profile saved', user => this.applyUser(user));
    }

    changePassword(): void {
        if (this.passwordForm.invalid) { return; }
        const { currentPassword, newPassword } = this.passwordForm.getRawValue();
        this.run(this.profile.changePassword(currentPassword, newPassword), 'Password changed', () => this.passwordForm.reset());
    }

    // ---- two-factor

    startTwoFactor(): void {
        this.run(this.profile.beginTwoFactor(), 'Scan the secret with your authenticator app', setup => (this.setup = setup));
    }

    enableTwoFactor(): void {
        if (this.twoFactorForm.invalid) { return; }
        this.run(this.profile.enableTwoFactor(this.twoFactorForm.getRawValue().code), 'Two-factor authentication is on', () => {
            this.setup = null;
            this.twoFactorForm.reset();
            this.reloadUser();
        });
    }

    disableTwoFactor(): void {
        if (this.disableForm.invalid) { return; }
        const { password, code } = this.disableForm.getRawValue();
        this.run(this.profile.disableTwoFactor(password, code), 'Two-factor authentication is off', () => {
            this.disableForm.reset();
            this.reloadUser();
        });
    }

    // ---- KYC

    onFile(event: Event): void {
        const input = event.target as HTMLInputElement;
        this.selectedFile = input.files?.[0] ?? null;
    }

    uploadDocument(): void {
        if (!this.selectedFile) { return; }
        const file = this.selectedFile;
        this.run(this.profile.uploadKycDocument(this.kycForm.getRawValue().documentType, file), 'Document uploaded for review', () => {
            this.selectedFile = null;
            this.loadKyc();
        });
    }

    // ---- closing the profile

    closeProfile(): void {
        if (this.closeForm.invalid) { return; }
        if (!confirm('Close your profile permanently? All accounts must have a zero balance.')) { return; }

        this.run(this.profile.closeProfile(this.closeForm.getRawValue().password), 'Your profile has been closed', () => {
            this.auth.logout();
            this.router.navigate(['/login']);
        });
    }

    private reloadUser(): void {
        this.profile.getProfile().subscribe(user => this.applyUser(user));
    }

    private run<T>(call: import('rxjs').Observable<T>, success: string, then: (result: T) => void): void {
        if (this.busy) { return; }
        this.busy = true;

        call.subscribe({
            next: result => {
                this.busy = false;
                this.snackBar.open(success, 'Close', { duration: 3000 });
                then(result);
            },
            error: error => {
                this.busy = false;
                this.snackBar.open(errorMessage(error), 'Close', { duration: 5000 });
            }
        });
    }
}
