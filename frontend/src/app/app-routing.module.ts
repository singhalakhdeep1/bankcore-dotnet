import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LoginComponent } from './components/auth/login/login.component';
import { RegisterComponent } from './components/auth/register/register.component';
import { DashboardComponent } from './components/dashboard/dashboard.component';
import { AccountsComponent } from './components/accounts/accounts.component';
import { AccountDetailComponent } from './components/account-detail/account-detail.component';
import { AuthGuard } from './guards/auth.guard';
import { RoleGuard } from './guards/role.guard';

const routes: Routes = [
    { path: '', redirectTo: '/dashboard', pathMatch: 'full' },
    { path: 'login', component: LoginComponent },
    { path: 'register', component: RegisterComponent },
    { path: 'dashboard', component: DashboardComponent, canActivate: [AuthGuard] },
    { path: 'accounts', component: AccountsComponent, canActivate: [AuthGuard] },
    { path: 'accounts/:id', component: AccountDetailComponent, canActivate: [AuthGuard] },
    {
        path: 'profile',
        canActivate: [AuthGuard],
        loadComponent: () => import('./components/profile/profile.component').then(m => m.ProfileComponent)
    },
    {
        path: 'payment-methods',
        canActivate: [AuthGuard],
        loadComponent: () => import('./components/payments/payment-pages').then(m => m.PaymentMethodsComponent)
    },
    {
        path: 'recurring',
        canActivate: [AuthGuard],
        loadComponent: () => import('./components/payments/payment-pages').then(m => m.RecurringPaymentsComponent)
    },
    {
        path: 'admin',
        canActivate: [AuthGuard, RoleGuard],
        data: { roles: ['Staff', 'Manager', 'Admin'] },
        loadComponent: () => import('./components/admin/admin-tabs').then(m => m.AdminComponent)
    },
    { path: '**', redirectTo: '/dashboard' }
];

@NgModule({
    imports: [RouterModule.forRoot(routes)],
    exports: [RouterModule]
})
export class AppRoutingModule { }
