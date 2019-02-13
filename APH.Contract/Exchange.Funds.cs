using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        public delegate object NEP5Contract(string method, object[] args);

        private static void IncreaseBalanceOf(byte[] assetId, byte[] userAddress, BigInteger adjustment)
        {
            byte[] userAssetKey = userAddress.Concat(assetId);
            BigInteger balance = Storage.Get(Storage.CurrentContext, userAssetKey).AsBigInteger();
            Storage.Put(Storage.CurrentContext, userAssetKey, balance + adjustment);
        }

        private static bool NoPullReduceBalanceOf(byte[] assetId, byte[] userAddress, BigInteger adjustment)
        {
            byte[] userAssetKey = userAddress.Concat(assetId);
            BigInteger balance = Storage.Get(Storage.CurrentContext, userAssetKey).AsBigInteger();
            if (adjustment > balance)
                return false;
            balance -= adjustment;
            if (balance == 0)
                Storage.Delete(Storage.CurrentContext, userAssetKey);
            else
                Storage.Put(Storage.CurrentContext, userAssetKey, balance.AsByteArray());
            return true;
        }

        private static bool PullNep5(byte[] assetId, byte[] fromAddress, BigInteger amountToPull)
        {
            byte[] assetSettings = Storage.Get(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(assetId));
            if (assetSettings.Length == 0)
            {
                Runtime.Notify("pullNep5Fail", "Invalid Asset", fromAddress, assetId, amountToPull);
                return false;
            }
            var transferMethod = assetSettings.Take(1) == USER_ASSET_TYPE_NEP5_EXTENSIONS
                ? "transferFrom" : "transfer";

            if (Nep5Transfer(assetId, fromAddress, ExecutionEngine.ExecutingScriptHash, transferMethod, amountToPull) == false)
                return false;

            AdjustTotalUserDexBalance(assetId, amountToPull);

            //Runtime.Notify("PullNEP5Token() succeeded", assetId, fromAddress, amountToPull);
            return true;
        }

        private static bool ReduceBalanceOf(byte[] assetId, byte[] userAddress, BigInteger adjustment)
        {
            byte[] userAssetKey = userAddress.Concat(assetId);
            BigInteger balance = Storage.Get(Storage.CurrentContext, userAssetKey).AsBigInteger();

            if (adjustment > balance)
            {
                if (assetId.Length != 20)
                {
                    //System assets can't be pullled
                    Runtime.Notify("ReduceBalanceOf() assetId length != 20", userAddress, assetId, adjustment);
                    return false;
                }

                BigInteger amountNeeded = adjustment - balance;
                if (PullNep5(assetId, userAddress, amountNeeded) == false)
                {
                    // Runtime.Notify("ReduceBalanceOf() Can't Pull NEP5", userAddress, assetId, balance, adjustment);
                    return false;
                }

                // Since we were committing more than was in the contract balance, the new contract balance is 0.
                // We can delte the storage value for the key instead of putting 0, to save GAS and storage usage.
                Storage.Delete(Storage.CurrentContext, userAssetKey);
                return true;
            }

            if (assetId.Length == 20)
            {
                // If we don't pull nep5 (checking witness) then we check it here explicitly (it's ok if it is redundant)
                if (Runtime.CheckWitness(userAddress) == false)
                    return false;
            }

            // if we got here adjustment will always be >= balance
            balance -= adjustment;
            if (balance == 0)
                Storage.Delete(Storage.CurrentContext, userAssetKey);
            else
                Storage.Put(Storage.CurrentContext, userAssetKey, balance.AsByteArray());
            return true;
        }

        private static void SetBalanceOfForWithdrawal(byte[] assetId, byte[] userAddress, BigInteger newBalance)
        {
            // Methods ...ForWithdrawal require additional logic due to the way that the contract owner APH balance
            // is stored differently than all other balances
            if (assetId == APH && userAddress == GetOwner())
            {
                // Special case for Exchange Owner's balance of APH, get from fee pool data (stored there to save gas during acceptOffers)
                byte[] poolData = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY);
                BigInteger poolFeesCollected = poolData.Range(0, 8).AsBigInteger();
                BigInteger belongsToContractOwner = poolData.Range(8, 8).AsBigInteger();

                if (newBalance < 0) newBalance = 0;
                belongsToContractOwner = newBalance;
                poolData = NormalizeBigIntegerTo8ByteArray(poolFeesCollected).Concat(NormalizeBigIntegerTo8ByteArray(belongsToContractOwner));
                Storage.Put(Storage.CurrentContext, FEES_POOL_KEY, poolData);
                return;
            }

            if (newBalance <= 0)
                DeleteUserValue(userAddress, assetId);
            else
                PutUserValue(userAddress, assetId, newBalance.AsByteArray());
            // Runtime.Notify("SetBalanceOf() balance set", assetId, newBalance, reservedBalance, userAddress);
        }

        private static bool Nep5Transfer(byte[] assetId, byte[] fromAddress, byte[] toAddress, string method, BigInteger amount)
        {
            var args = new object[] { fromAddress, toAddress, amount };
            var contract = (NEP5Contract)assetId.ToDelegate();
            bool transferSuccessful = (bool)contract(method, args);
            if (transferSuccessful == false)
            {
                Runtime.Notify("sendNep5Fail", "Transfer failed", assetId, fromAddress, toAddress, amount);
                return false;
            }

            return true;
        }

        private static bool SendNep5(byte[] toAddress, byte[] assetId, BigInteger amount)
        {
            byte[] assetSettings = Storage.Get(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(assetId));
            if (assetSettings.Length == 0)
            {
                Runtime.Notify("sendNep5Fail", "Invalid Asset", toAddress, assetId, amount);
                return false;
            }

            if (Nep5Transfer(assetId, ExecutionEngine.ExecutingScriptHash, toAddress, "transfer", amount) == false)
                return false;

            AdjustTotalUserDexBalance(assetId, 0 - amount);

            // Runtime.Notify("SendNEP5Token() succeeded", assetId, toAddress, amount);
            return true;
        }

        private static bool ValidateClaimGas(Transaction tx)
        {
            if (VerifyOwner() == false && VerifyManager() == false) return false;

            TransactionOutput[] outputs = tx.GetOutputs();

            foreach (var o in outputs)
            {
                if (o.AssetId != GAS || o.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                   return false;
            }

            if (tx.Type != ClaimTransactionType)
            {
                return false;
            }

            return true;
        }
    }
}
