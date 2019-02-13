using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        public class Offer
        {
            public byte[] Creator;
            public BigInteger QuantityToBuy;
            public BigInteger QuantityToSell;
            public byte[] AssetIdToBuy;
            public byte[] AssetIdToSell;
            public BigInteger Nonce;
        }

        private static bool AddOffer(object[] args, byte[] sender)
        {
            object[] notifyArgs;
            string firstNotifyArg = "addOfferInitError";

            Offer offer = new Offer();
            offer.Creator = sender;
            offer.AssetIdToBuy = (byte[])args[0];
            BigInteger quantityToBuy = (BigInteger) args[1];
            offer.QuantityToBuy = quantityToBuy;

            offer.AssetIdToSell = (byte[])args[2];
            BigInteger quantityToSell = (BigInteger)args[3];
            offer.QuantityToSell = quantityToSell;
            offer.Nonce = (BigInteger)args[4];

            if (offer.AssetIdToBuy.Length != 32 && offer.AssetIdToBuy.Length != 20 ||
                offer.AssetIdToSell.Length != 32 && offer.AssetIdToSell.Length != 20)
            {
                notifyArgs = new object[] { null, "Invalid Asset Length", sender, offer.AssetIdToBuy, offer.AssetIdToSell };
                goto AddOfferErrorNotify;
            }

            if (quantityToBuy <= 0 || quantityToSell <= 0)
            {
                notifyArgs = new object[] { null, "No negative quantities allowed", sender, quantityToBuy, quantityToSell };
                goto AddOfferErrorNotify;
            }

            byte[] offerData = offer.Serialize();

            byte[] offerId = Hash256(offerData);
            byte[] offerKey = PREFIX_OFFERS.Concat(offerId);

            byte[] existingOfferData = Storage.Get(Storage.CurrentContext, offerKey);
            if (existingOfferData.Length > 0)
            {
                notifyArgs = new object[] { null, "Offer already exists", sender, offerId };
                goto AddOfferErrorNotify;
            }

            firstNotifyArg = "addOfferError";

            var existingWhitelistedUserIdentity = Storage.Get(Storage.CurrentContext, PREFIX_WHITELIST.Concat(sender));
            if (existingWhitelistedUserIdentity.Length == 0)
            {
                notifyArgs = new object[] { null, "Not whitelisted", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell };
                goto AddOfferErrorNotify;
            }

            //validate this is a market we currently support and the order size is enough
            Market offerMarket = GetMarket(offer.AssetIdToBuy, offer.AssetIdToSell);
            if (offerMarket.QuoteAssetId.Length == 0)
            {
                notifyArgs = new object[] { null, "Invalid market", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell };
                goto AddOfferErrorNotify;
            }

            BigInteger minimumTickSize = offerMarket.MinimumTickSize;

            if (offer.AssetIdToBuy == offerMarket.QuoteAssetId)
            {
                if (quantityToBuy < offerMarket.MinimumSize)
                {
                    notifyArgs = new object[] { null, "Insufficient order size", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell, quantityToBuy, minimumTickSize };
                    goto AddOfferErrorNotify;
                }

                BigInteger unitPrice = (quantityToSell * ONE) / quantityToBuy;
                if ((unitPrice / minimumTickSize) * minimumTickSize != unitPrice)
                {
                    notifyArgs = new object[] { null, "Invalid tick size", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell, unitPrice, minimumTickSize };
                    goto AddOfferErrorNotify;
                }
            }
            else
            {
                if (quantityToSell < offerMarket.MinimumSize)
                {
                    notifyArgs = new object[] { null, "Insufficient order size", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell, quantityToSell, minimumTickSize };
                    goto AddOfferErrorNotify;
                }

                BigInteger unitPrice = (quantityToBuy * ONE) / quantityToSell;
                if ((unitPrice / minimumTickSize) * minimumTickSize != unitPrice)
                {
                    notifyArgs = new object[] { null, "Invalid tick size", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell, unitPrice, minimumTickSize };
                    goto AddOfferErrorNotify;
                }
            }

            bool reduced = ReduceBalanceOf(offer.AssetIdToSell, sender, quantityToSell);
            if (reduced == false)
            {
                notifyArgs = new object[] { null, "Unable to reserve asset", sender, offerId, offer.AssetIdToBuy, offer.AssetIdToSell, quantityToSell };
                goto AddOfferErrorNotify;
            }

            Storage.Put(Storage.CurrentContext, offerKey, offerData);
            // Runtime.Notify("PostOffer", sender, offerId, offerData);
            Runtime.Notify("offerCreated", offerId, offer.Creator, offer.AssetIdToBuy, quantityToBuy, offer.AssetIdToSell, quantityToSell);

            return true;
AddOfferErrorNotify:
            notifyArgs[0] = firstNotifyArg;
            Runtime.Notify(notifyArgs);
            return false;
        }

        private static bool AcceptOffer(object[] args)
        {
            byte[] offerIdToAccept = (byte[])args[0];
            byte[] takerAddress = (byte[])args[1];
            byte[] assetIdToGive = (byte[])args[2];
            BigInteger quantityToGive = (BigInteger)args[3];
            byte[] assetIdToReceive = (byte[])args[4];
            BigInteger quantityToReceive = (BigInteger)args[5];
            object[] notifyArgs;

            Market offerMarket = GetMarket(assetIdToGive, assetIdToReceive);

            if (offerMarket.QuoteAssetId.Length == 0)
            {
                notifyArgs = new object[] { null, "acceptOfferInvalidMarket", takerAddress, assetIdToGive, assetIdToReceive };
                goto AcceptOfferErrorNotify;
            }

            BigInteger quoteQuantity = assetIdToGive == offerMarket.QuoteAssetId ? quantityToGive : quantityToReceive;
            BigInteger baseQuantity = assetIdToGive == offerMarket.QuoteAssetId ? quantityToReceive : quantityToGive;

            if (Runtime.CheckWitness(takerAddress) == false)
            {
                notifyArgs = new object[] { null, "takerAddress not a witness", takerAddress, offerIdToAccept, quoteQuantity };
                goto AcceptOfferErrorNotify;
            }

            byte[] offerKey = PREFIX_OFFERS.Concat(offerIdToAccept);
            byte[] offerData = Storage.Get(Storage.CurrentContext, offerKey);
            if (offerData.Length == 0)
            {
                if ((bool)args[6] == true) // createIfMissing
                {
                    BigInteger nonce = (BigInteger)args[7];
                    Runtime.Notify("acceptOfferCreatesOffer", "OfferId not found; Creating a new offer", takerAddress, offerIdToAccept, quoteQuantity);
                    return AddOffer(new object[]
                                    {
                                        assetIdToReceive, quantityToReceive,
                                        assetIdToGive, quantityToGive,
                                        nonce
                                    }, takerAddress);
                }

                notifyArgs = new object[] { null, "OfferId not found", takerAddress, offerIdToAccept, quoteQuantity, args[6] };
                goto AcceptOfferErrorNotify;
            }

            Offer offer = (Offer) offerData.Deserialize();

            if (offer.AssetIdToBuy != assetIdToGive)
            {
                notifyArgs = new object[] { null, "Invalid asset to give", takerAddress, offerIdToAccept, quoteQuantity, offer.AssetIdToBuy, assetIdToGive };
                goto AcceptOfferErrorNotify;
            }
            if (offer.AssetIdToSell != assetIdToReceive)
            {
                notifyArgs = new object[] { null, "Invalid asset to receive", takerAddress, offerIdToAccept, quoteQuantity, offer.AssetIdToSell, assetIdToReceive };
                goto AcceptOfferErrorNotify;
            }

            if (quantityToGive <= 0)
            {
                notifyArgs = new object[] { null, "Invalid quantity to give, <= 0", takerAddress, offerIdToAccept, quoteQuantity, quantityToGive };
                goto AcceptOfferErrorNotify;
            }
            if (quantityToReceive <= 0)
            {
                notifyArgs = new object[] { null, "Invalid quantity to receive, <= 0", takerAddress, offerIdToAccept, quoteQuantity, quantityToReceive };
                goto AcceptOfferErrorNotify;
            }

            if (quantityToGive > offer.QuantityToBuy)
            {
                // Runtime.Notify(acceptOfferErrorStr, "Invalid quantity to give, more than offered", takerAddress, offerIdToAccept, quoteQuantity, offer.QuantityToBuy);
                quantityToGive = offer.QuantityToBuy;
                quantityToReceive = (offer.QuantityToBuy / quantityToGive) * quantityToReceive;
            }
            else if (quantityToGive < offer.QuantityToBuy)
            {
                if (offer.AssetIdToBuy == offerMarket.QuoteAssetId)
                {
                    if (offer.QuantityToBuy - quantityToGive < offerMarket.MinimumSize)
                    {
                        notifyArgs = new object[] { null, "Would leave <min size", takerAddress, offerIdToAccept, quoteQuantity, offer.AssetIdToBuy, offer.QuantityToBuy - quantityToGive, offerMarket.MinimumSize };
                        goto AcceptOfferErrorNotify;
                    }
                }
                else
                {
                    if (offer.QuantityToSell - quantityToReceive < offerMarket.MinimumSize)
                    {
                        notifyArgs = new object[] { null, "Would leave <min size", takerAddress, offerIdToAccept, quoteQuantity, offer.AssetIdToSell, offer.QuantityToSell - quantityToReceive, offerMarket.MinimumSize };
                        goto AcceptOfferErrorNotify;
                    }
                }
            }


            BigInteger calculatedQuantityToReceive = (quantityToGive * offer.QuantityToSell) / offer.QuantityToBuy;
            if (quantityToReceive != calculatedQuantityToReceive)
            {
                notifyArgs = new object[] { null, "Receive Quantity != calculated", takerAddress, offerIdToAccept, quoteQuantity, offerIdToAccept, quantityToReceive, calculatedQuantityToReceive };
                goto AcceptOfferErrorNotify;
            }

            BigInteger feePercentage = assetIdToGive == offerMarket.QuoteAssetId ? offerMarket.SellFeePercent : offerMarket.BuyFeePercent;

            BigInteger baseAssetToAphConversionRate = GetAssetToAphConversionRate(offerMarket.BaseAssetId);

            BigInteger fee = baseQuantity * baseAssetToAphConversionRate * feePercentage / ONE / ONE;

            // Take away what the taker sold
            if (NoPullReduceBalanceOf(assetIdToGive, takerAddress, quantityToGive) == false)
            {
                notifyArgs = new object[] { null, "Insufficient balance of asset", takerAddress, offerIdToAccept, quoteQuantity, assetIdToGive };
                goto AcceptOfferErrorNotify;
            }

            // ** FEES **
            // if (fee > 0) // Removed - SetMarket gaurantees fees cannot be negative, and we need to reduce worst case GAS cost.

            //charge the fees to the taker
            if (NoPullReduceBalanceOf(APH, takerAddress, fee) == false)
            {
                //refund the asset the taker sold, wasn't able to charge fees
                IncreaseBalanceOf(assetIdToGive, takerAddress, quantityToGive);
                notifyArgs = new object[] { null, "Insufficient APH for trade fee", takerAddress, offerIdToAccept, quoteQuantity, assetIdToGive };
                goto AcceptOfferErrorNotify;
            }

            // Give them to the distribution pool
            FeeCollected(fee);

            // ** END FEES **

            // Give the maker what they bought
            IncreaseBalanceOf(assetIdToGive, offer.Creator, quantityToGive);

            offer.QuantityToSell -= quantityToReceive;
            offer.QuantityToBuy -= quantityToGive;

            // Update to close out the order
            if (offer.QuantityToBuy == 0)
            {
                Storage.Delete(Storage.CurrentContext, offerKey);
            }
            else
            {
                byte[] updatedData = offer.Serialize();
                Storage.Put(Storage.CurrentContext, offerKey, updatedData);
            }

            // Give the taker what they bought (do last in case the taker somehow maliciously causes the invocation to run out of gas, they won't get what they bought)
            IncreaseBalanceOf(assetIdToReceive, takerAddress, quantityToReceive);

            Runtime.Notify( "offerAccepted", takerAddress, offerIdToAccept, offer.QuantityToBuy, offer.QuantityToSell);

            return true;
AcceptOfferErrorNotify:
            notifyArgs[0] = "acceptOfferError";
            Runtime.Notify(notifyArgs);
            return false;
        }

        private static bool CancelOffer(object[] args, byte[] sender)
        {
            if (args.Length != 1) return false;

            byte[] offerIdToCancel = (byte[])args[0];
            string errMsg = "offerId not found";

            if (offerIdToCancel.Length != 32)
                goto CancelOfferError;
            // We use a prefix and verify the length to ensure the onwer can't potentially use a bogus offerId of
            // an asset setting and make a fake offer to increase their balance of an asset
            byte[] offerKey = PREFIX_OFFERS.Concat(offerIdToCancel);

            byte[] offerData = Storage.Get(Storage.CurrentContext, offerKey);
            if (offerData.Length == 0)
                goto CancelOfferError;

            Offer offer = (Offer) offerData.Deserialize();

            if (Runtime.CheckWitness(offer.Creator) == false && VerifyOwner() == false && VerifyManager() == false)
            {
                errMsg = "No permission";
                goto CancelOfferError;
            }

            IncreaseBalanceOf(offer.AssetIdToSell, offer.Creator, offer.QuantityToSell);


            Storage.Delete(Storage.CurrentContext, offerKey);

            Runtime.Notify("offerCanceled", offer.Creator, offerIdToCancel);

            return true;
CancelOfferError:
            Runtime.Notify("cancelOfferError", errMsg, sender, offerIdToCancel);
            return false;
        }

        private static bool DepositNep5(byte[] assetId, BigInteger quantity, byte[] sender)
        {
            string errMsg;

            // Removing this check to reduce gas, but if owner deposits NEP5, it will burn Owner tokens.
            // Owner depositing NEP5 tokens? (slap... bad owner, bad.)
            /*
            if (VerifyOwner())
            {
                Runtime.Notify(depositErrorStr, "Owner can't deposit NEP5.");
                return false;
            }
            */

            if (quantity <= 0)
            {
                errMsg = "quantity <= 0";
                goto DepositError;
            }

            // Rely on PullNep5 will check the witness of the caller.
            if (PullNep5(assetId, sender, quantity) == false)
            {
                errMsg = "Deposit() NEP5 token failed";
                goto DepositError;
            }

            IncreaseBalanceOf(assetId, sender, quantity);
            Runtime.Notify("deposit", sender, assetId, quantity);
            return true;
DepositError:
            Runtime.Notify("depositError", errMsg, sender, assetId, quantity);
            return false;
        }

        private static void AdjustTotalUserDexBalance(byte[] assetId, BigInteger adjustment)
        {
            byte[] totalUserAssetBalanceKey = assetId.Concat(POSTFIX_ASSET_USER_BALANCES);
            BigInteger totalDexBalance = Storage.Get(Storage.CurrentContext, totalUserAssetBalanceKey).AsBigInteger();
            totalDexBalance = totalDexBalance + adjustment;
            if (totalDexBalance < 0)
            {
                // This shouldn't happen; places that use it already verify the amounts, so should be safe to remove.
                totalDexBalance = 0;
            }
            Storage.Put(Storage.CurrentContext, totalUserAssetBalanceKey, totalDexBalance.AsByteArray());
        }

        private static bool OnTokenTransfer(byte[] assetId, object[] args)
        {
            if (args.Length < 3) return false;
            object[] notifyArgs;

            byte[] sender = (byte[])args[0];
            byte[] to = (byte[])args[1];
            BigInteger quantity = (BigInteger)args[2];

            if (to != ExecutionEngine.ExecutingScriptHash)
            {
                notifyArgs = new object[]
                    { null, "Transfer not sent to us", sender, assetId, to, ExecutionEngine.ExecutingScriptHash };
                goto OnTokenTransferErrorNotify;
            }

            if (quantity <= 0)
            {
                notifyArgs = new object[]
                    { null, "quantity <= 0", sender, assetId, quantity };
                goto OnTokenTransferErrorNotify;
            }

            var existingWhitelistedUserIdentity = Storage.Get(Storage.CurrentContext, PREFIX_WHITELIST.Concat(sender));
            if (existingWhitelistedUserIdentity.Length == 0)
            {
                notifyArgs = new object[]
                    { null, "not whitelisted", sender, assetId, quantity };
                goto OnTokenTransferErrorNotify;
            }

            // AssetId must be the asset of the calling script hash

            IncreaseBalanceOf(assetId, sender, quantity);

            AdjustTotalUserDexBalance(assetId, quantity);

            Runtime.Notify("deposit", sender, assetId, quantity);
            return true;
OnTokenTransferErrorNotify:
            notifyArgs[0] = "tokenTranferError";
            Runtime.Notify(notifyArgs);
            return false;
        }
    }
}
