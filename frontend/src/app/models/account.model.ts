export interface Account {
    id: string;
    accountNumber: string;
    accountType: string;
    balance: number;
    currency: string;
    status: string;
    createdAt: string;
}

export interface CreateAccountRequest {
    accountType: string;
    currency?: string;
}
