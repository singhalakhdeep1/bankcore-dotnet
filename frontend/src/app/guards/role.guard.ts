import { Injectable } from '@angular/core';
import { ActivatedRouteSnapshot, CanActivate, Router } from '@angular/router';
import { AuthService } from '../services/auth.service';

/** Lets a route through only for users whose role is listed in the route's data.roles. The API enforces the same rules. */
@Injectable({ providedIn: 'root' })
export class RoleGuard implements CanActivate {
    constructor(private auth: AuthService, private router: Router) { }

    canActivate(route: ActivatedRouteSnapshot): boolean {
        const roles = (route.data['roles'] as string[] | undefined) ?? [];
        const role = this.auth.currentUserValue?.role;

        if (this.auth.isAuthenticated() && role && roles.includes(role)) {
            return true;
        }

        this.router.navigate(['/dashboard']);
        return false;
    }
}
