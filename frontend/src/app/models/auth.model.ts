export interface User {
    id: string;
    email: string;
    firstName: string;
    lastName: string;
    phone?: string;
    role: string;
    kycStatus: string;
    isActive: boolean;
    twoFactorEnabled?: boolean;
    address?: string;
    city?: string;
    state?: string;
    country?: string;
    postalCode?: string;
    createdAt: string;
}

export interface AuthResponse {
    accessToken: string;
    refreshToken: string;
    tokenType: string;
    user: User;
}

export interface LoginRequest {
    email: string;
    password: string;
    totpCode?: string;
}

export interface RegisterRequest {
    email: string;
    password: string;
    firstName: string;
    lastName: string;
    phone?: string;
}
