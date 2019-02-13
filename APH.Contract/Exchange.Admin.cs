using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        private static bool SetOwner(byte[] newOwner)
        {
            if (VerifyOwner() == false)
                return false;

            Storage.Put(Storage.CurrentContext, "owner", newOwner);
            Runtime.Notify("setOwner", newOwner);
            return true;
        }

        private static bool SetManager(byte[] newManager)
        {
            if (VerifyOwner() == false)
                return false;

            Storage.Put(Storage.CurrentContext, "manager", newManager);
            Runtime.Notify("setManager", newManager);
            return true;
        }

        private static bool ReclaimOrphanFunds(byte[] assetId)
        {
            if (VerifyOwner() == false)
                return false;

            byte[] totalUserAssetBalanceKey = assetId.Concat(POSTFIX_ASSET_USER_BALANCES);
            BigInteger totalDexBalance = Storage.Get(Storage.CurrentContext, totalUserAssetBalanceKey).AsBigInteger();

            BigInteger contractsBalance;
            if (assetId.Length == 20)
            {
                var args = new object[] {ExecutionEngine.ExecutingScriptHash};
                var contract = (NEP5Contract) assetId.ToDelegate();
                contractsBalance = (BigInteger) contract("balanceOf", args);
            }
            else if (assetId.Length == 32)
            {
                Account account = Blockchain.GetAccount(ExecutionEngine.ExecutingScriptHash);
                contractsBalance = account.GetBalance(assetId);
            }
            else
                return false;

            BigInteger orphanAmount = contractsBalance - totalDexBalance;

            if (orphanAmount <= 0)
            {
                Runtime.Notify("reclaimFail", "Nothing to reclaim", orphanAmount);
                return false;
            }

            // We have an amount that belongs to the contract (was transferred to it's address), but wasn't accounted
            // for by the contract account for it in the running total
            totalDexBalance = totalDexBalance + orphanAmount;
            Storage.Put(Storage.CurrentContext, totalUserAssetBalanceKey, totalDexBalance.AsByteArray());

            // Use this two step method rather than the IncreaseBalanceOf method so it will go into the
            // special key (used for the fee pool) if it is APH
            BigInteger currentOwnerBalance = GetBalanceOfForWithdrawal(assetId, GetOwner());
            currentOwnerBalance = currentOwnerBalance + orphanAmount;
            SetBalanceOfForWithdrawal(assetId, GetOwner(), currentOwnerBalance);

            Runtime.Notify("reclaimedOrphanedFunds", assetId, orphanAmount);
            return true;
        }

        private static bool SetAssetSettings(byte[] assetId, byte[] settings)
        {
            if (VerifyOwner() == false || (assetId.Length != 20 && assetId.Length != 32))
                return false;

            // Byte 0 (First byte): Transfer type to use. If not set, or set to 0, it will use NEP5 'transfer'
            // Byte 1-8: Withdraw fee in APH satoshis.
            Storage.Put(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(assetId), settings);
            Runtime.Notify("setAssetSettings", settings);
            return true;
        }

        private static bool AphNotify(object[] args)
        {
            if (VerifyOwner() == false)
            {
                Runtime.Notify("aphNotify", "No permission");
                return false;
            }

            object[] finalArgs = new object[args.Length+1];
            finalArgs[0] = "aphNotify";
            for (int i = 0; i < args.Length; i++)
                finalArgs[i + 1] = args[i];
            Runtime.Notify(finalArgs);
            return true;
        }

        /*
        public static bool MigrateContract(object[] args)
        {
            if (args.Length != 6)
            {
                Runtime.Notify("MigrateContract() requires 6 params.");
                return false;
            }

            if (VerifyOwner() == false)
            {
                Runtime.Notify("MigrateContract() only supported by owner.");
                return false;
            }

            byte[] newScript = (byte[])args[0];
            string newName = (string)args[1];
            string newVersion = (string)args[2];
            string newAuthor = (string)args[3];
            string newEmail = (string)args[4];
            string newDescription = (string)args[5];

            Runtime.Notify("Starting Upgrade");
            Neo.SmartContract.Framework.Services.Neo.Contract.Migrate(newScript, new byte[] { 0x07, 0x10 }, 0x05,
                            true, newName, newVersion, newAuthor, newEmail, newDescription);
            Runtime.Notify("Upgrade Complete");
            return true;
        }
        */
    }
}
