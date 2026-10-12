export interface Account {
    id: string;
    accountNumber: string;
    accountType: string;
    balance: number;
    currency: string;
    status: string;
    interestRate: number;
    createdAt: string;
}

export interface CreateAccountRequest {
    accountType: string;
    currency?: string;
}

export const ACCOUNT_TYPES = ['Checking', 'Savings', 'Business', 'FixedDeposit'] as const;
export const CURRENCIES = ['USD', 'EUR', 'GBP', 'INR'] as const;

export interface Transaction {
    id: string;
    accountId: string;
    type: string;
    /** Signed: credits are positive, debits negative. */
    amount: number;
    currency: string;
    balanceBefore: number;
    balanceAfter: number;
    status: string;
    description?: string;
    referenceNumber: string;
    toAccountNumber?: string;
    fromAccountNumber?: string;
    createdAt: string;
}

export interface PagedResult<T> {
    items: T[];
    page: number;
    pageSize: number;
    totalCount: number;
    totalPages: number;
}

export interface TransferRequest {
    fromAccountId: string;
    toAccountNumber: string;
    amount: number;
    description?: string;
    idempotencyKey?: string;
}

export interface TransferResult {
    from: Transaction;
    to: Transaction;
}

export interface LedgerResponse {
    reconciled: boolean;
    entries: LedgerEntry[];
}

export interface LedgerEntry {
    id: string;
    transactionId?: string;
    debit: number;
    credit: number;
    balanceAfter: number;
    description: string;
    createdAt: string;
}

export interface PaymentRequest {
    accountId: string;
    amount: number;
    merchantName: string;
    description?: string;
}

export interface PaymentResult {
    success: boolean;
    transactionId: string;
    status?: string;
    amount: number;
    currency: string;
    errorMessage?: string;
}
