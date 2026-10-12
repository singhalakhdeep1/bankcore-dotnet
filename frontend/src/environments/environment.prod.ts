export const environment = {
    production: true,
    // Served behind the nginx container, which proxies /api to the backend.
    apiUrl: '/api',
    stripeKey: ''
};
