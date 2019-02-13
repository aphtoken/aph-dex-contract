using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        private static readonly byte AttributeUsage_SignatureRequestType = 0xA1;  // 161
        private static readonly byte AttributeUsage_WithdrawAddress = 0xA2;       // 162
        private static readonly byte AttributeUsage_WithdrawSystemAssetId = 0xA3; // 163
        private static readonly byte AttributeUsage_WithdrawNEP5AssetId = 0xA4;   // 164
        private static readonly byte AttributeUsage_WithdrawAmount = 0xA5;        // 165
        private static readonly byte AttributeUsage_WithdrawValidUntil = 0xA6;    // 166

        private static readonly byte[] SignatureRequestTypeWithdrawStepMark = { 0x91 }; // 145
        private static readonly byte[] SignatureRequestTypeWithdrawStepWithdraw = { 0x92 };
        private static readonly byte[] SignatureRequestTypeClaimSend = { 0x93 };
        private static readonly byte[] SignatureRequestTypeClaimGas = { 0x94 };

        private static readonly byte InvocationTransactionType = 0xd1;
        private static readonly byte ClaimTransactionType = 0x02;
        private static readonly byte[] WithdrawArgs = { 0x00, 0xc1, 0x08, 0x77, 0x69, 0x74, 0x68, 0x64, 0x72, 0x61, 0x77 }; // PUSH0, PACK, PUSHBYTES8, "withdraw" as bytes
        private static readonly byte[] ClaimArgs = { 0x00, 0xc1, 0x05, 0x63, 0x6c, 0x61, 0x69, 0x6d }; // PUSH0, PACK, PUSHBYTES5, "claim" as bytes
        private static readonly byte[] OpCodeAppCall = { 0x67 };

        public static readonly long MAX_APH_WITHDRAW_FEE = 10000000000;

        private static bool ValidateSignatureRequest()
        {
            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;

            var attributes = tx.GetAttributes();
            var requestTypeOrStep = EMPTY;
            foreach (var attr in attributes)
            {
                if (attr.Usage != AttributeUsage_SignatureRequestType) continue;
                requestTypeOrStep = attr.Data.Take(1);
                break;
            }

            if (requestTypeOrStep == SignatureRequestTypeClaimGas)
                return ValidateClaimGas(tx);

            TransactionInput[] inputs = tx.GetInputs();
            TransactionOutput[] inputReferences = tx.GetReferences();
            TransactionOutput[] outputs = tx.GetOutputs();
            byte[] executingScriptHash = ExecutionEngine.ExecutingScriptHash;

            InvocationTransaction invocationTransaction = (InvocationTransaction)tx;
            // check if invocation transaction is invoking claim
            if (invocationTransaction.Script == ClaimArgs.Concat(OpCodeAppCall).Concat(executingScriptHash))
            {
                byte[] address = ReadWithdrawToAddress(tx);
                // don't allow any inputs to the contract for claim.
                return ValidateWithdrawInputsOutputs(address, inputs, inputReferences, outputs, EMPTY, 0, 0, false);
            }

            string verifyWithdrawInitFail = "verifyWithdrawInitFail";
            if (tx.Type != InvocationTransactionType) return false;

            if (invocationTransaction.Script != WithdrawArgs.Concat(OpCodeAppCall).Concat(executingScriptHash))
            {
                Runtime.Notify(verifyWithdrawInitFail, "Invalid Params", invocationTransaction.Script, WithdrawArgs);
                return false;
            }

            // This is used to break up UTXOs or send NEO or to self to be able to claim full gas amount accrued.
            if (requestTypeOrStep == SignatureRequestTypeClaimSend)
            {
                // ValidateClaimSignatureRequest -- Inlined to save space
                if (VerifyOwner() == false && VerifyManager() == false) return false;

                if (ValidateWithdrawInputsOutputs(GetManager(), inputs, inputReferences, outputs, EMPTY, 0, long.MaxValue, false) == false)
                {
                    // ValidateWithdrawInputsOutputs already notifies of the error.
                    return false;
                }

                return true;
            }

            // ValidateWithdrawSignatureRequest -- Inline to save space
            object[] notifyArgs;
            byte[] toAddress = ReadWithdrawToAddress(tx);
            if (Runtime.CheckWitness(toAddress) == false && VerifyOwner() == false && VerifyManager() == false)
            {
                notifyArgs = new object[] { null, "no permission", toAddress };
                goto NotifyVerifyWithdrawFail;
            }


            var assetId = ReadWithdrawAssetId(tx);
            var isWithdrawingNep5 = assetId.Length == 20;
            var amount = ReadWithdrawAmount(tx, assetId);

            BigInteger withdrawAllowedAmount;
            // Improved logic to allow for the user to pay GAS fees and accept change
            if (requestTypeOrStep == SignatureRequestTypeWithdrawStepMark || isWithdrawingNep5)
                withdrawAllowedAmount = 0;
            else
                withdrawAllowedAmount = amount;

            var maxContractNeoGasInput = isWithdrawingNep5 ? 0 : amount;
            bool isWithdrawStep = requestTypeOrStep == SignatureRequestTypeWithdrawStepWithdraw;

            if (ValidateWithdrawInputsOutputs(toAddress, inputs, inputReferences, outputs, assetId, withdrawAllowedAmount, maxContractNeoGasInput, isWithdrawStep) == false)
            {
                // ValidateWithdrawInputsOutputs already notifies of the error.
                return false;
            }

            BigInteger currentHeight = Blockchain.GetHeight();
            var validUntilHeight = ReadWithdrawValidUntil(tx);


            if (currentHeight > validUntilHeight)
            {
                notifyArgs = new object[] { null, "Valid Until Height < Current Height", toAddress, validUntilHeight, currentHeight };
                goto NotifyVerifyWithdrawFail;
            }

            if (requestTypeOrStep == SignatureRequestTypeWithdrawStepMark)
            {
                // ValidateMarkStep -- Inlined reduces unneeded function call.
                if (isWithdrawingNep5)
                {
                    notifyArgs = new object[] { null, "Mark not valid for NEP5", toAddress, outputs[0].AssetId };
                    goto NotifyVerifyWithdrawFail;
                }

                var balance = GetBalanceOfForWithdrawal(assetId, toAddress);
                if (balance < amount)
                {
                    notifyArgs = new object[] { null, "Insufficient balance", toAddress, balance, amount };
                    goto NotifyVerifyWithdrawFail;
                }

                bool foundRequestedOutput = false;
                foreach (var o in outputs)
                {
                    if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;
                    if (o.Value != amount) continue;

                    foundRequestedOutput = true;
                }

                if (foundRequestedOutput == false)
                {
                    notifyArgs = new object[] { null, "No matching output", toAddress, amount };
                    goto NotifyVerifyWithdrawFail;
                }

                return true;
            }

            if (isWithdrawStep)
            {
                if (isWithdrawingNep5 == false)
                {
                    int withdrawInputCount = 0;

                    for (int inputIndex = 0; inputIndex < inputReferences.Length; inputIndex++)
                    {
                        var inputReference = inputReferences[inputIndex];
                        if (inputReference.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                            continue;

                        withdrawInputCount++;
                    }

                    if (withdrawInputCount != 1)
                    {
                        notifyArgs = new object[] { null, "1 input required", toAddress };
                        goto NotifyVerifyWithdrawFail;
                    }

                    return true;
                }

                foreach (var o in outputs)
                {
                    if (o.AssetId != GAS)
                    {
                        notifyArgs = new object[] { null, "NEP5 withdraws only can use GAS outputs", toAddress, o.AssetId };
                        goto NotifyVerifyWithdrawFail;
                    }

                    if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;
                    notifyArgs = new object[] { null, "NEP5 can't have outputs to the contract.", toAddress, o.AssetId };
                    goto NotifyVerifyWithdrawFail;
                }

                return true;
            }

            notifyArgs = new object[] { "Withdraw Validate, Invalid Step", toAddress, requestTypeOrStep, SignatureRequestTypeWithdrawStepMark, SignatureRequestTypeWithdrawStepWithdraw };
NotifyVerifyWithdrawFail:
            notifyArgs[0] = "verifyWithdrawFail";
            Runtime.Notify(notifyArgs);
            return false;
        }

        private static bool ValidateWithdrawInputsOutputs(byte[] toAddress, TransactionInput[] inputs,
            TransactionOutput[] inputReferences, TransactionOutput[] outputs, byte[] withdrawAssetId,
            BigInteger withdrawAmount, BigInteger maxContractInputAmount, bool isWithdrawStep)
        {
            ulong gasInFromContract = 0;
            ulong neoInFromContract = 0;

            BigInteger withdrawGas = withdrawAssetId == GAS ? withdrawAmount : 0;
            BigInteger withdrawNeo = withdrawAssetId == NEO ? withdrawAmount : 0;
            string verifyWithdrawFailStr = "verifyWithdrawFail";

            for (int i = 0; i < inputReferences.Length; i++)
            {
                var inputRef = inputReferences[i];
                if (inputRef.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;

                if (gasInFromContract + neoInFromContract >= maxContractInputAmount)
                {
                    Runtime.Notify(verifyWithdrawFailStr, "Exceeded allowed inputs.", toAddress);
                    return false;
                }
                if (inputRef.AssetId == GAS) gasInFromContract += (ulong)inputRef.Value;
                else if (inputRef.AssetId == NEO) neoInFromContract += (ulong)inputRef.Value;

                var input = inputs[i];
                byte[] key = input.PrevHash.Concat(ShortToByteArray(input.PrevIndex));
                var reservedFor = Storage.Get(Storage.CurrentContext, key);
                if (isWithdrawStep == false && reservedFor.Length <= 0) continue;
                if (isWithdrawStep && reservedFor == toAddress) continue;

                Runtime.Notify(verifyWithdrawFailStr, "Input already reserved", toAddress, key, reservedFor);
                return false;
            }

            ulong gasOutToContract = 0;
            ulong neoOutToContract = 0;

            foreach (var o in outputs)
            {
                if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;

                if (o.AssetId == GAS) gasOutToContract += (ulong)o.Value;
                else if (o.AssetId == NEO) neoOutToContract += (ulong)o.Value;
            }

            if (gasInFromContract != gasOutToContract + withdrawGas)
            {
                // The utxo that should be going to the user is the withdrawAmount here
                Runtime.Notify(verifyWithdrawFailStr, "GAS input != expected output", toAddress, gasInFromContract, gasOutToContract, withdrawGas);
                return false;
            }

            if (neoInFromContract != neoOutToContract + withdrawNeo)
            {
                // The utxo that should be going to the user is the withdrawAmount here
                Runtime.Notify(verifyWithdrawFailStr, "NEO input != expected output", toAddress, neoInFromContract, neoOutToContract, withdrawNeo);
                return false;
            }
            return true;
        }

        private static bool ExecuteWithdraw()
        {
            Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
            var attributes = tx.GetAttributes();
            var step = EMPTY;
            var toAddress = EMPTY;
            var assetId = EMPTY;
            BigInteger amount = 0;
            // var step = ReadSignatureRequestType(tx);
            // var toAddress = ReadWithdrawToAddress(tx);
            // var assetId = ReadWithdrawAssetId(tx);
            // Use 1 loop to save gas.
            foreach (var attr in attributes)
            {
                if (attr.Usage == AttributeUsage_SignatureRequestType)
                {
                    step = attr.Data.Take(1);
                } else if (attr.Usage == AttributeUsage_WithdrawAddress)
                {
                    toAddress = attr.Data.Take(20);
                } else if (attr.Usage == AttributeUsage_WithdrawSystemAssetId)
                {
                    assetId = attr.Data.Take(32);
                }
                else if (attr.Usage == AttributeUsage_WithdrawNEP5AssetId)
                {
                    assetId = attr.Data.Take(20);
                }
                else if (attr.Usage == AttributeUsage_WithdrawAmount)
                {
                    amount = attr.Data.Take(8).AsBigInteger();
                }
            }

            object[] notifyArgs;

            if (step == SignatureRequestTypeWithdrawStepWithdraw)
            {
                if (assetId.Length == 20)
                {
                    byte[] userAssetBalanceKey = toAddress.Concat(assetId);
                    BigInteger balance;
                    bool isOwnerWithdrawingAph = false;
                    if (assetId == APH && toAddress == GetOwner())
                    {
                        isOwnerWithdrawingAph = true;
                        byte[] poolData = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY);
                        balance = poolData.Range(8, 8).AsBigInteger();
                    }
                    else
                        balance = Storage.Get(Storage.CurrentContext, userAssetBalanceKey).AsBigInteger();

                    if (balance < amount)
                    {
                        notifyArgs = new object[] { null, "Withdraw NEP5 insufficent balance", toAddress, balance, amount };
                        goto NotifyWithdrawFail;
                    }

                    var transferSucceeded = SendNep5(toAddress, assetId, amount);
                    if (transferSucceeded == false)
                    {
                        notifyArgs = new object[] { null, "executeWithdraw NEP5 Transfer failed", toAddress, assetId, amount };
                        goto NotifyWithdrawFail;
                    }

                    balance -= amount;
                    if (isOwnerWithdrawingAph)
                        SetBalanceOfForWithdrawal(assetId, toAddress, balance);
                    else
                    {
                        if (balance <= 0)
                            Storage.Delete(Storage.CurrentContext, userAssetBalanceKey);
                        else
                            Storage.Put(Storage.CurrentContext, userAssetBalanceKey, balance.AsByteArray());
                    }
                }
                else
                {
                    TransactionInput[] inputs = tx.GetInputs();
                    if (assetId.Length != 32)
                    {
                        notifyArgs = new object[] { null, "Mark assetId len != 32", toAddress };
                        goto NotifyWithdrawFail;
                    }

                    DeleteUserValue(toAddress, assetId.Concat(POSTFIX_USER_ASSET_WITHDRAWING));
                    // NOTE: Could verify the amount passed equals the amount from the user value, but not required since
                    //       the amount from the Runtime.Notify is not used for non-nep5 withdraws.

                    // Delete the marked UTXOs
                    foreach (var input in inputs)
                    {
                        // Note this includes inputs not from the contract; but we won't have anything in storage for them
                        // in that case, so it won't be an issue.
                        Storage.Delete(Storage.CurrentContext, input.PrevHash.Concat(ShortToByteArray(input.PrevIndex)));
                    }
                }

                Runtime.Notify("withdraw", toAddress, assetId, amount);
                return true;
            }


            if (step == SignatureRequestTypeWithdrawStepMark)
            {
                // ExecuteMarkStep -- INLINE to save space
                TransactionOutput[] outputs = tx.GetOutputs();

                if (assetId.Length == 20)
                {
                    // NOTE: this is probably dead code because verify step wouldn't let it get this far.
                    notifyArgs = new object[] { null, "Can't mark NEP5", toAddress };
                    goto NotifyWithdrawFail;

                }

                if (assetId.Length != 32)
                {
                    notifyArgs = new object[] { null, "Mark assetId len != 32", toAddress };
                    goto NotifyWithdrawFail;
                }

                byte[] utxoAmountKey = toAddress.Concat(assetId).Concat(POSTFIX_USER_ASSET_WITHDRAWING);
                var withdrawingUTXOAmountBytes = Storage.Get(Storage.CurrentContext, utxoAmountKey);

                if (withdrawingUTXOAmountBytes.Length > 0)
                {
                    notifyArgs = new object[] { null, "Already withdrawing", toAddress, withdrawingUTXOAmountBytes };
                    goto NotifyWithdrawFail;

                }

                var balance = GetBalanceOfForWithdrawal(assetId, toAddress);
                if (balance < amount)
                {
                    notifyArgs = new object[] { null, "Insufficient balance", toAddress, balance, amount };
                    goto NotifyWithdrawFail;
                }

                // Check if there is a withdraw fee imposed
                byte[] assetSettings = Storage.Get(Storage.CurrentContext, PREFIX_ASSET_SETTINGS.Concat(assetId));
                if (assetSettings.Length > 8)
                {
                    BigInteger aphFee = assetSettings.Range(1, 8).AsBigInteger();
                    if (aphFee > MAX_APH_WITHDRAW_FEE) aphFee = MAX_APH_WITHDRAW_FEE;
                    if (aphFee > 0)
                    {
                        // Everyone other than owner has to pay withdraw fee if one applies.
                        if (toAddress != GetOwner() && NoPullReduceBalanceOf(APH, toAddress, aphFee) == false)
                        {
                            notifyArgs = new object[] { null, "Insufficient APH for withdraw Fee", toAddress, aphFee };
                            goto NotifyWithdrawFail;
                        }
                    }
                }

                balance -= amount;
                SetBalanceOfForWithdrawal(assetId, toAddress, balance);

                var reserved = SetOutputReservedFor(tx, outputs, amount, toAddress);
                if (reserved == false)
                {
                    notifyArgs = new object[] { null, "Mark Step, failed to reserve output UTXO", toAddress };
                    goto NotifyWithdrawFail;

                }

                // MarkForWithdraw
                AdjustTotalUserDexBalance(assetId, 0 - amount);

                // Save the balance being withdrawn for this user in storage so it can be read later
                Storage.Put(Storage.CurrentContext, utxoAmountKey, amount.ToByteArray());

                Runtime.Notify("withdrawMark", toAddress, assetId, amount);
                return true;
            }

            Runtime.Notify("withdrawFailInvalidStep", step);
            return false;
NotifyWithdrawFail:
            notifyArgs[0] = "withdrawFail";
            Runtime.Notify(notifyArgs);
            return false;
        }

        public static BigInteger GetBalanceOfForWithdrawal(byte[] assetId, byte[] userAddress)
        {
            // Methods ...ForWithdrawal require additional logic due to the way that the contract owner APH balance is
            // stored differently than all other balances

            if (assetId == APH && userAddress == GetOwner())
            {
                // Special case for Exchange Owner's balance of APH, get from fee pool data (stored there to save gas
                // during acceptOffers)
                byte[] poolData = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY);
                return poolData.Range(8, 8).AsBigInteger();
            }
            return GetUserValue(userAddress, assetId).AsBigInteger();
        }

        private static byte[] ReadWithdrawToAddress(Transaction transaction)
        {
            var attributes = transaction.GetAttributes();
            foreach (var attr in attributes)
            {
                if (attr.Usage == AttributeUsage_WithdrawAddress)
                {
                    return attr.Data.Take(20);
                }
            }
            return EMPTY;
        }

        private static byte[] ReadWithdrawAssetId(Transaction transaction)
        {
            var attributes = transaction.GetAttributes();
            foreach (var attr in attributes)
            {
                if (attr.Usage == AttributeUsage_WithdrawSystemAssetId)
                {
                    return attr.Data.Take(32);
                }
                else if (attr.Usage == AttributeUsage_WithdrawNEP5AssetId)
                {
                    return attr.Data.Take(20);
                }
            }
            return EMPTY;
        }

        private static BigInteger ReadWithdrawAmount(Transaction transaction, byte[] assetId)
        {
            BigInteger amount = 0;
            var attributes = transaction.GetAttributes();
            foreach (var attr in attributes)
            {
                if (attr.Usage == AttributeUsage_WithdrawAmount)
                {
                    amount = attr.Data.Take(8).AsBigInteger();
                    break;
                }
            }

            if (assetId == NEO)
            {
                // NOTE: Maybe NEO will be divisible in the future; but probably not until NEO 3.0
                // neo must be a whole number
                amount = amount / 100000000 * 100000000;
            }

            return amount;
        }

        private static BigInteger ReadWithdrawValidUntil(Transaction transaction)
        {
            var attributes = transaction.GetAttributes();
            foreach (var attr in attributes)
            {
                if (attr.Usage == AttributeUsage_WithdrawValidUntil)
                {
                    return attr.Data.Take(8).AsBigInteger();
                }
            }
            return 0;
        }

        private static bool SetOutputReservedFor(Transaction tx, TransactionOutput[] outputs, BigInteger amount, byte[] reservedForAddress)
        {
            for (ushort index = 0; index < outputs.Length; index++)
            {
                TransactionOutput output = outputs[index];
                if (output.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;
                if (output.Value != amount) continue;

                // Save the UTXO for this user address in storage so it can be read later
                Storage.Put(Storage.CurrentContext, tx.Hash.Concat(ShortToByteArray(index)), reservedForAddress);
                Runtime.Notify("utxoReserved", tx.Hash.Concat(ShortToByteArray(index)), reservedForAddress);
                return true;
            }
            return false;
        }
    }
}
