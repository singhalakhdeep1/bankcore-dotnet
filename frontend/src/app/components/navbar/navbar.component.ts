import { Component } from '@angular/core';
import { Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';

@Component({
    selector: 'app-navbar',
    templateUrl: './navbar.component.html',
    styleUrls: ['./navbar.component.scss']
})
export class NavbarComponent {
    constructor(
        public authService: AuthService,
        private router: Router
    ) { }

    get isStaff(): boolean {
        return ['Staff', 'Manager', 'Admin'].includes(this.authService.currentUserValue?.role ?? '');
    }

    logout(): void {
        // Revoke the tokens on the server too, then clear them locally.
        this.authService.logoutRemote().subscribe(() => this.router.navigate(['/login']));
    }
}
