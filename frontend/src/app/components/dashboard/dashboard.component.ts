import { Component, OnInit } from '@angular/core';
import { AuthService } from '../../services/auth.service';
import { User } from '../../models/auth.model';

@Component({
    selector: 'app-dashboard',
    templateUrl: './dashboard.component.html',
    styleUrls: ['./dashboard.component.scss']
})
export class DashboardComponent implements OnInit {
    user: User | null = null;

    constructor(private authService: AuthService) { }

    ngOnInit(): void {
        this.user = this.authService.currentUserValue;
    }
}
