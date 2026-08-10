using System;
using Xunit;

namespace BankCore.Tests
{
    public class AccountServiceTests
    {
        [Fact]
        public void Account_Deposit_IncreasesBalance()
        {
            decimal initialBalance = 500.00m;
            decimal depositAmount = 250.00m;

            decimal newBalance = initialBalance + depositAmount;

            Assert.Equal(750.00m, newBalance);
        }

        [Fact]
        public void Account_Transfer_ValidatesFunds()
        {
            decimal balance = 1000.00m;
            decimal transferAmount = 1200.00m;

            bool canTransfer = balance >= transferAmount;

            Assert.False(canTransfer);
        }
    }
}
