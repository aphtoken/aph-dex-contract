using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        public static ulong development_version = 120050116;

        /// <summary>
        /// Main method of a contract. ParameterList: 0710, ReturnType: 05
        /// </summary>
        /// <param name="operation">Method to invoke</param>
        /// <param name="args">Method parameters</param>
        /// <returns>Method's return value or false if operation is invalid</returns>
        public static object Main(string operation, params object[] args)
        {
            if (Runtime.Trigger == TriggerType.Application)
            {
                // This is here because it is the most GAS intensive operation, moved to top to avoid all the other comparisons
                if (operation == "acceptOffer") return AcceptOffer(args);
                if (operation == "withdraw") return ExecuteWithdraw();

                if (operation == "getBalance") return GetBalanceOfForWithdrawal((byte[])args[0], (byte[])args[1]);
                if (operation == "getContributed") return GetContributed((byte[])args[0]);
                if (operation == "getAvailableToClaim") return GetAvailableToClaim((byte[])args[0]);
                if (operation == "getAphConversionRate") return GetAssetToAphConversionRate((byte[]) args[0]);

                byte[] unverifiedSender = EMPTY;
                Transaction tx = (Transaction) ExecutionEngine.ScriptContainer;

                // first check to see if the transaction has a valid sender attribute (and verify that address is also a witness)
                TransactionAttribute[] attributes = tx.GetAttributes();

                foreach (TransactionAttribute attr in attributes)
                {
                    if (attr.Usage == SENDER_ATTRIBUTE_USAGE && attr.Data != ExecutionEngine.ExecutingScriptHash)
                    {
                        unverifiedSender = attr.Data;
                        break;
                    }
                }

                if (operation == "addOffer")
                {
                    if (args.Length != 5) return false;

                    byte[] assetIdToSell = (byte[]) args[2];
                    // We count on ReduceBalanceOf verifying the sender.
                    // Optimize gas usage for adding an offer that must pull a NEP5 asset
                    if (assetIdToSell.Length == 20) return AddOffer(args, unverifiedSender);
                } else if (operation == "deposit")
                {
                    if (args.Length != 2) return false;

                    byte[] assetId = (byte[])args[0];
                    var existingWhitelistedUserIdentity = Storage.Get(Storage.CurrentContext,
                        PREFIX_WHITELIST.Concat(unverifiedSender));
                    if (existingWhitelistedUserIdentity.Length == 0)
                    {
                        Runtime.Notify("depositError", "not whitelisted", unverifiedSender, assetId,
                            (BigInteger) args[1]);
                        return false;
                    }

                    if (assetId.Length == 20)
                    {
                        // Optimize gas usage for NEP5 deposit. We count on PullNep5 to check the witness.
                        BigInteger quantity = (BigInteger)args[1];
                        return DepositNep5(assetId, quantity, unverifiedSender);
                    }
                    if (assetId.Length != 32) return false;
                    // for assetId.Length == 32, deposit will be handled after CaptureSystemAssetsSent
                }

                // ExecutionEngine.CallingScriptHash needs to be captured here in order to get the script hash of the
                // NEP5 token which is dynamically calling us
                if (operation == "onTokenTransfer") return OnTokenTransfer(ExecutionEngine.CallingScriptHash, args);

                byte[] sender;

                // byte[] sender = GetSender(); // GetSender() now inlined in the below block, since occurred only once.
                {
                    sender = EMPTY;
                    if (attributes.Length > MAX_ATTRIBUTES)
                    {
                        // Prevent execution with too many attributes, may be a potential attack vector to cause the contract to run out of GAS
                        return false;
                    }

                    if (Runtime.CheckWitness(unverifiedSender))
                    {
                        sender = unverifiedSender;
                        // Runtime.Notify("sender not a witness", sender);
                        goto CaptureSystemAssetsSent; // return sender;
                    }


                    foreach (TransactionAttribute a in attributes)
                    {
                        if (a.Usage == SENDER_ATTRIBUTE_USAGE && a.Data != unverifiedSender)
                        {
                            if (Runtime.CheckWitness(a.Data) == false)
                            {
                                // Runtime.Notify("sender not a witness", sender);
                                continue;
                            }
                            sender = a.Data;

                            //Runtime.Notify("GetSender() sender", sender);
                            goto CaptureSystemAssetsSent; // return sender;
                        }
                    }

                    //otherwise get the sender from an input reference (they sent a system asset along with the invocation)
                    TransactionOutput[] references = tx.GetReferences();
                    if (references.Length > MAX_REFERENCES)
                    {
                        //prevent execution with too many references, may be a potential attack vector to cause the contract to run out of GAS
                        goto CaptureSystemAssetsSent; // return EMPTY;
                    }

                    foreach (TransactionOutput r in references)
                    {
                        //Runtime.Notify("GetSender() sender", references[0].ScriptHash);
                        if (r.ScriptHash != ExecutionEngine.ExecutingScriptHash)
                        {
                            sender = r.ScriptHash;
                            goto CaptureSystemAssetsSent; // return sender;
                        }
                    }

                    Runtime.Notify("no sender");
                    goto NoSenderRequiredOperations; // return;
                }

CaptureSystemAssetsSent: // inline due to only a single call
                // Only deposit and addOffer can accept NEO or GAS funds and credit balances.
                if (operation != "deposit" && operation != "addOffer") goto RunOperation;

                //private static void CaptureSystemAssetsSent(byte[] sender)
                long neoSentToContract = 0;
                long gasSentToContract = 0;
                {
                    TransactionOutput[] outputs = tx.GetOutputs();

                    if (outputs.Length == 0)
                    {
                        goto AddOfferOrDeposit; // return;
                    }

                    foreach (TransactionOutput o in outputs)
                    {
                        if (o.ScriptHash != ExecutionEngine.ExecutingScriptHash) continue;

                        if (o.AssetId == NEO) neoSentToContract += o.Value;
                        if (o.AssetId == GAS) gasSentToContract += o.Value;
                    }

                    if (neoSentToContract > 0 || gasSentToContract > 0)
                    {
                        TransactionOutput[] inputReferences = tx.GetReferences();
                        if (neoSentToContract > 0)
                        {
                            foreach (TransactionOutput r in inputReferences)
                            {
                                if (r.ScriptHash != sender && r.AssetId == NEO)
                                {
                                    //this wasn't sent from the sender
                                    neoSentToContract -= r.Value;
                                }
                            }

                            if (neoSentToContract > 0)
                            {
                                //Runtime.Notify("CaptureSystemAssetsSent() NEO sent to contract.", sender, neoSentToContract);
                                IncreaseBalanceOf(NEO, sender, neoSentToContract);
                                // Add to user balances total owned by the exchange
                                AdjustTotalUserDexBalance(NEO, neoSentToContract);
                            }
                        }
                        if (gasSentToContract > 0)
                        {
                            foreach (TransactionOutput r in inputReferences)
                            {
                                if (r.ScriptHash != sender && r.AssetId == GAS)
                                {
                                    //this wasn't sent from the sender
                                    gasSentToContract -= r.Value;
                                }
                            }

                            if (gasSentToContract > 0)
                            {
                                //Runtime.Notify("CaptureSystemAssetsSent() GAS sent to contract.", sender, gasSentToContract);
                                IncreaseBalanceOf(GAS, sender, gasSentToContract);

                                // Add to user balances total owned by the exchange
                                AdjustTotalUserDexBalance(GAS, gasSentToContract);
                            }
                        }
                    }
                }

AddOfferOrDeposit:
                if (operation == "addOffer") return AddOffer(args, sender);
                if (operation == "deposit")
                {
                    // already added to user's balance in CaptureSystemAssetsSent() method
                    byte[] assetId = (byte[])args[0];

                    long amountDeposited;
                    if (assetId == NEO )
                    {
                        amountDeposited = neoSentToContract;
                    } else if (assetId == GAS)
                    {
                        amountDeposited = gasSentToContract;
                    }
                    else
                        return false;

                    Runtime.Notify("deposit", sender, assetId, amountDeposited);
                    return true;
                }

RunOperation:
                // Guaranteed to have sender for these
                if (operation == "commit") return Commit(args, sender);
                if (operation == "claim") return Claim(sender);
                if (operation == "compound") return Compound(sender);
                if (operation == "cancelOffer") return CancelOffer(args, sender);
                if (operation == "send")
                {
                    if (VerifyOwner() == false || VerifyManager() == false) return false;
                    if (args.Length != 3) return false;
                    byte[] assetId = (byte[]) args[0];
                    if (assetId.Length != 20 && assetId.Length != 32) return false;
                    BigInteger quantity = (BigInteger) args[1];
                    if (quantity <= 0) return false;
                    byte[] toAddress = (byte[]) args[2];
                    if (toAddress.Length != 20) return false;
                    var senderBalance = GetBalanceOfForWithdrawal(assetId, sender);
                    if (senderBalance < quantity) return false;
                    senderBalance -= quantity;
                    SetBalanceOfForWithdrawal(assetId, sender, senderBalance);
                    var receiverBalance = GetBalanceOfForWithdrawal(assetId, toAddress);
                    receiverBalance += quantity;
                    SetBalanceOfForWithdrawal(assetId, toAddress, receiverBalance);
                    Runtime.Notify("sent", sender, toAddress, quantity);
                    return true;
                }

NoSenderRequiredOperations:
                bool succeeded = false;
                StorageContext storageContext = Storage.CurrentContext;

                if (operation == "setOwner") succeeded = SetOwner((byte[])args[0]);
                else if (operation == "setManager") succeeded = SetManager((byte[])args[0]);
                else if (operation == "setMarket") succeeded = SetMarket(args);
                else if (operation == "closeMarket") succeeded = CloseMarket(args);
                else if (operation == "setAssetToAphRate")
                    succeeded = SetAssetToAphConversionRate((byte[]) args[0], (BigInteger) args[1]);
                else if (operation == "setFeeRedistributionPercentage") succeeded = SetFeeRedistributionPercentage((BigInteger)args[0]);
                else if (operation == "setClaimMinimumBlocks") succeeded = SetClaimMinimumBlocks((BigInteger)args[0]);
                else if (operation == "reclaimOrphanFunds") succeeded = ReclaimOrphanFunds((byte[])args[0]);
                else if (operation == "setAssetSettings") succeeded = SetAssetSettings((byte[]) args[0], (byte[]) args[1]);
                else if (operation == "addIdentity")
                {
                    if (Runtime.CheckWitness(GetWhitelister()) == false) return false;
                    if (args.Length < 4) return false;

                    byte[] userIdentityHash = (byte[]) args[0];
                    if (userIdentityHash.Length != 32) return false;
                    byte[] userInfoHash1 = (byte[]) args[1];
                    if (userInfoHash1.Length != 0 && userInfoHash1.Length != 32) return false;
                    byte[] userInfoHash2 = (byte[]) args[2];
                    if (userInfoHash2.Length != 0 && userInfoHash2.Length != 32) return false;
                    byte[] userScriptHash = (byte[]) args[3];
                    if (userScriptHash.Length != 20) return false;
                    byte[] miscUserInfo;
                    if (args.Length > 4)
                        miscUserInfo = (byte[]) args[4];
                    else
                        miscUserInfo = new byte[0];
                    var userIdentity = new UserIdentity();
                    userIdentity.HashInfo1 = userInfoHash1;
                    userIdentity.HashInfo2 = userInfoHash2;
                    userIdentity.ScriptHash = userScriptHash;
                    userIdentity.MiscUserInfo = miscUserInfo;

                    var existingIdentity =
                        Storage.Get(storageContext, PREFIX_USERIDENTITY.Concat(userIdentityHash));
                    if (existingIdentity.Length != 0)
                    {
                        var existingUserIdentity = (UserIdentity) existingIdentity.Deserialize();
                        if (existingUserIdentity.ScriptHash != userScriptHash)
                        {
                            // if this identity already exists but associated with a different address
                            // De-whitelist the previously associated address for this identity
                            Storage.Delete(storageContext,
                                PREFIX_WHITELIST.Concat(existingUserIdentity.ScriptHash));
                        }
                    }

                    var identityData = userIdentity.Serialize();
                    Storage.Put(Storage.CurrentContext, PREFIX_USERIDENTITY.Concat(userIdentityHash), identityData);
                    Runtime.Notify("setIdentity", userIdentityHash, userInfoHash1, userInfoHash2, userScriptHash, miscUserInfo);
                    return true;
                }
                else if (operation == "whitelistAddress")
                {
                    if (Runtime.CheckWitness(GetWhitelister()) == false
                        && Runtime.CheckWitness(GetOwner()) == false) return false;
                    if (args.Length != 2) return false;
                    byte[] userScriptHash = (byte[])args[0];
                    if (userScriptHash.Length != 20) return false;
                    byte[] userIdentityHash = (byte[])args[1];
                    if (userIdentityHash.Length != 32) return false;

                    object[] notifyArgs;
                    var existingIdentity =
                        Storage.Get(storageContext, PREFIX_USERIDENTITY.Concat(userIdentityHash));
                    if (existingIdentity.Length == 0)
                    {
                        notifyArgs = new object[] { null, "missing identity", userIdentityHash };
                        goto WhitelistErrorNotify;
                    }

                    var userIdentity = (UserIdentity) existingIdentity.Deserialize();
                    // If identity passed belongs to a different address.
                    if (userIdentity.ScriptHash != userScriptHash)
                    {
                        notifyArgs = new object[] { null, "address mismatch", userIdentityHash, userScriptHash,
                            userIdentity.ScriptHash };
                        goto WhitelistErrorNotify;
                    }

                    // check if existing address already whitelisted
                    var existingWhitelistedUserIdentity =
                        Storage.Get(storageContext, userScriptHash);
                    if (existingWhitelistedUserIdentity.Length != 0)
                    {
                        if (existingWhitelistedUserIdentity != userIdentityHash)
                        {
                            // fail since this address is already whitelisted with a different identity
                            // The identity currently owning this address should first be switched to a different
                            // address, which will de-whitelist this address and allow it to be associated with a
                            // different identity.
                            notifyArgs = new object[] { null, "already whitelisted", userIdentityHash, userScriptHash };
                            goto WhitelistErrorNotify;
                        }
                    }
                    else
                    {
                        Storage.Put(storageContext, PREFIX_WHITELIST.Concat(userScriptHash), userIdentityHash);
                    }

                    Runtime.Notify("whitelisted", userScriptHash, userIdentityHash);
                    return true;
WhitelistErrorNotify:
                    notifyArgs[0] = "whitelistFail";
                    Runtime.Notify(notifyArgs);
                    return false;
                }
                else if (operation == "blacklistAddress")
                {
                    if (Runtime.CheckWitness(GetWhitelister()) == false
                        && Runtime.CheckWitness(GetOwner()) == false) return false;
                    byte[] userScriptHash = (byte[])args[0];
                    if (userScriptHash.Length != 20) return false;
                    // just remove from whitelist
                    Storage.Delete(storageContext, PREFIX_WHITELIST.Concat(userScriptHash));
                    Runtime.Notify("blacklisted", userScriptHash);
                }
                else if (operation == "aphNotify") succeeded = AphNotify(args);
                else if (operation == "setWhitelister")
                {
                    if (VerifyOwner() == false)
                    {
                        // Runtime.Notify("setManagerFail", "No perms", newManager);
                        return false;
                    }

                    Storage.Put(storageContext, "whitelister", (byte[]) args[0]);
                    Runtime.Notify("setWhitelister", (byte[]) args[0]);
                    return true;
                }
                // Don't support contract migrate -- too dangerous if keys fell into wrong hands --
                /* else if (operation == "migrate") return MigrateContract(args); */
                else
                {
                    Runtime.Notify(operation + " - Unknown operation.");
                }

                Runtime.Notify(succeeded ? "Success" : "Failure", operation);

                return succeeded;
            }

            if (Runtime.Trigger == TriggerType.Verification)
            {
                return ValidateSignatureRequest();
            }

            if (Runtime.Trigger == TriggerType.VerificationR)
            {
                Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
                if (tx.Type != InvocationTransactionType)
                {
                    Runtime.Notify("Send must use Invocation TX", tx.Type);
                    return false;
                }

                if (operation == "acceptOffer"
                        || operation == "addOffer"
                        || operation == "withdraw"
                        || operation == "onTokenTransfer"
                        || operation == "getBalance"
                        || operation == "getContributed"
                        || operation == "getAvailableToClaim"
                        || operation == "getAphConversionRate")
                {
                    Runtime.Notify("OP can't accept sent funds.", operation);
                    return false;
                }

                return true;
            }

            return false;
        }
    }
}

public class UserIdentity
{
    public byte[] HashInfo1;
    public byte[] HashInfo2;
    public byte[] ScriptHash;
    public byte[] MiscUserInfo;
}
