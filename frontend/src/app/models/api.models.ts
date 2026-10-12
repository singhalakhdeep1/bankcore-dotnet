// Shapes returned by the BankCore API beyond accounts and auth (see account.model.ts and auth.model.ts).

export interface PaymentMethod {
    id: string;
    provider: string;
    brand: string;
    last4: string;
    expMonth: number;
    expYear: number;
    isDefault: boolean;
    createdAt: string;
}

export interface AddPaymentMethodRequest {
    provider: string;
    cardNumber?: string;
    providerToken?: string;
    expMonth: number;
    expYear: number;
    cvc?: string;
    makeDefault: boolean;
}

export interface PaymentRecord {
    transactionId: string;
    kind: string;
    provider?: string;
    mode?: string;
    amount: number;
    currency: string;
    fee: number;
    merchantName: string;
    status: string;
    refundAmount?: number;
    errorMessage?: string;
    createdAt: string;
}

export interface RecurringPayment {
    id: string;
    accountId: string;
    merchantName: string;
    amount: number;
    currency: string;
    frequency: string;
    nextRunAt: string;
    endAt?: string;
    status: string;
    consecutiveFailures: number;
    lastRunAt?: string;
    lastResult?: string;
}

export interface BankTransfer {
    id: string;
    accountId: string;
    rail: string;
    amount: number;
    fee: number;
    currency: string;
    beneficiaryName: string;
    reference: string;
    status: string;
    failureReason?: string;
    submittedAt: string;
    expectedSettlementAt: string;
    settledAt?: string;
}

export interface BankTransferRequest {
    accountId: string;
    rail: string;
    amount: number;
    beneficiaryName: string;
    beneficiaryAccountNumber?: string;
    routingNumber?: string;
    swiftBic?: string;
    iban?: string;
    destinationCountry?: string;
    memo?: string;
}

export interface Chargeback {
    id: string;
    paymentReference: string;
    amount: number;
    reason: string;
    status: string;
    resolutionNotes?: string;
    openedAt: string;
    resolvedAt?: string;
}

export interface KycDocument {
    id: string;
    documentType: string;
    fileName: string;
    contentType: string;
    sizeBytes: number;
    status: string;
    reviewNotes?: string;
    uploadedAt: string;
    reviewedAt?: string;
}

export interface TwoFactorSetup {
    secret: string;
    otpAuthUri: string;
}

export interface PageResult<T> {
    items: T[];
    page: number;
    pageSize: number;
    totalCount: number;
}

// ---- back office

export interface AdminUser {
    id: string;
    email: string;
    firstName: string;
    lastName: string;
    role: string;
    kycStatus: string;
    isActive: boolean;
    isLocked: boolean;
    lockoutEnd?: string;
    twoFactorEnabled: boolean;
    accountCount: number;
    lastLoginAt?: string;
    createdAt: string;
}

export interface PendingDocument {
    id: string;
    userId: string;
    userEmail: string;
    documentType: string;
    fileName: string;
    contentType: string;
    sizeBytes: number;
    uploadedAt: string;
}

export interface ComplianceAlert {
    id: string;
    accountId: string;
    transactionId?: string;
    alertType: string;
    severity: string;
    riskScore: number;
    description: string;
    status: string;
    reviewNotes?: string;
    createdAt: string;
}

export interface SanctionedEntity {
    id: string;
    name: string;
    accountNumber?: string;
    country?: string;
    listName: string;
    isActive: boolean;
}

export interface RegulatoryReport {
    id: string;
    type: string;
    periodStart: string;
    periodEnd: string;
    itemCount: number;
    generatedAt: string;
    payload?: unknown;
}

export interface TrialBalanceLine {
    account: string;
    currency: string;
    debits: number;
    credits: number;
    net: number;
}

export interface TrialBalance {
    asOf: string;
    lines: TrialBalanceLine[];
    totals: { currency: string; debits: number; credits: number; balanced: boolean }[];
    balanced: boolean;
}

export interface BalanceSheetSection {
    currency: string;
    assets: Record<string, number>;
    liabilities: Record<string, number>;
    retainedEarnings: number;
    totalAssets: number;
    totalLiabilities: number;
    balanced: boolean;
}

export interface LedgerPeriod {
    id: string;
    name: string;
    startDate: string;
    endDate: string;
    status: string;
    totalDebits: number;
    totalCredits: number;
    closedAt?: string;
}

export interface Reconciliation {
    accountsChecked: number;
    clean: boolean;
    mismatches: { accountNumber: string; storedBalance: number; ledgerBalance: number }[];
}

export interface PaymentAnalytics {
    totalPayments: number;
    completed: number;
    failed: number;
    successRate: number;
    completedVolume: number;
    refundedVolume: number;
    gatewayFees: number;
    chargebacksOpened: number;
    chargebackRate: number;
    byStatus: Record<string, number>;
}

export interface AuditEntry {
    id: string;
    timestamp: string;
    userId?: string;
    action: string;
    entityType?: string;
    entityId?: string;
    details?: string;
    ipAddress?: string;
}
