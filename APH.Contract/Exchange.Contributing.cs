using Neo.SmartContract.Framework;
using Neo.SmartContract.Framework.Services.Neo;
using Neo.SmartContract.Framework.Services.System;
using System.Numerics;

namespace APH.Contract
{
    public partial class Exchange : SmartContract
    {
        public class Contribution
        {
            public byte[] UserAddress;
            public BigInteger UnitsContributed;
            public BigInteger ContributionHeight;
            public BigInteger CompoundHeight;
            public BigInteger FeesCollectedSnapshot;
            public BigInteger FeeUnitsSnapshot;
        }

        public class ContributionSums
        {
            public BigInteger TotalUnitsContributed;
            public BigInteger LastAppliedFeeSnapshot;
            public BigInteger TotalFeeUnits;
        }

        private static BigInteger GetFeeRedistributionPercentage()
        {
            BigInteger percentage = Storage.Get(Storage.CurrentContext, "feeRedistributionPercentage").AsBigInteger();
            if (percentage == 0)
            {
                return DEFAULT_FEE_REDISTRIBUTION_PERCENTAGE;
            }

            return percentage;
        }
        private static BigInteger GetClaimMinimumBlocks()
        {
            BigInteger blocks = Storage.Get(Storage.CurrentContext, "claimMinimumBlocks").AsBigInteger();
            if (blocks == 0)
            {
                return DEFAULT_CLAIM_MINIMUM_BLOCKS;
            }

            return blocks;
        }
        private static bool SetFeeRedistributionPercentage(BigInteger percentage)
        {
            if (VerifyOwner() == false)
                return false;

            Storage.Put(Storage.CurrentContext, "feeRedistributionPercentage", percentage);
            return true;
        }
        private static bool SetClaimMinimumBlocks(BigInteger blocks)
        {
            if (VerifyManager() == false && VerifyOwner() == false)
                return false;

            Storage.Put(Storage.CurrentContext, "claimMinimumBlocks", blocks);
            return true;
        }


        private static void FeeCollected(BigInteger amount)
        {
            BigInteger addToRedistributionPool = (amount * GetFeeRedistributionPercentage()) / 100;
            BigInteger giveToContractOwner = amount - addToRedistributionPool;

            byte[] poolData = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY);
            BigInteger belongsToPool = poolData.Range(0, 8).AsBigInteger();
            BigInteger belongsToContractOwner = poolData.Range(8, 8).AsBigInteger();

            // Keep a running total of all fees ever collected (used for staking logic)
            belongsToPool += addToRedistributionPool;
            belongsToContractOwner += giveToContractOwner;
            poolData = NormalizeBigIntegerTo8ByteArray(belongsToPool).Concat(NormalizeBigIntegerTo8ByteArray(belongsToContractOwner));
            Storage.Put(Storage.CurrentContext, FEES_POOL_KEY, poolData);
            // Runtime.Notify("PutValue", FEES_REDISTRIBUTION_POOL_KEY, pool);
            // Runtime.Notify("feeCollected", addToRedistributionPool, belongsToPool, giveToContractOwner, belongsToContractOwner);
        }

        private static void UpdateTotalFeeUnits(BigInteger totalFeesCollected, ContributionSums contributionSums)
        {
            if (contributionSums.LastAppliedFeeSnapshot < totalFeesCollected)
            {
                BigInteger feesCollectedSinceLastSnapshot =
                    totalFeesCollected - contributionSums.LastAppliedFeeSnapshot;
                BigInteger feeUnits = contributionSums.TotalUnitsContributed * feesCollectedSinceLastSnapshot;
                contributionSums.TotalFeeUnits = contributionSums.TotalFeeUnits + feeUnits;
                Runtime.Notify("updateTotalFeeUnits", contributionSums.TotalFeeUnits, feeUnits,
                    feesCollectedSinceLastSnapshot, contributionSums.TotalUnitsContributed, totalFeesCollected);
                contributionSums.LastAppliedFeeSnapshot = totalFeesCollected;
            }
        }

        private static bool Commit(object[] args, byte[] sender)
        {
            if (args.Length != 1)
            {
                // Runtime.Notify("Commit() requires 1 param", args.Length);
                return false;
            }

            object[] notifyArgs;
            if(VerifyOwner())
            {
                notifyArgs = new object[] { null, "Owner can't commit", sender };
                goto CommitErrorNotify;
            }

            BigInteger quantity = (BigInteger) args[0];

            Contribution contribution = GetContribution(sender);
            if (contribution.UnitsContributed > 0)
            {
                notifyArgs = new object[] { null, "Already committed. Claim first.", sender, contribution.UnitsContributed };
                goto CommitErrorNotify;
            }

            BigInteger totalFeesCollected = BigIntegerFrom8Bytes(
                Storage.Get(Storage.CurrentContext, FEES_POOL_KEY).Range(0, 8));
            ContributionSums contributionSums = GetContributionSums();

            // Update total fee units if fees have been collected since the last applied fee snapshot
            UpdateTotalFeeUnits(totalFeesCollected, contributionSums);

            // Reduce APH balance
            if (quantity <= 0 || ReduceBalanceOf(APH, sender, quantity) == false)
            {
                notifyArgs = new object[] { null, "Insufficient balance.", sender, quantity, GetUserValue(sender, APH).AsBigInteger() };
                goto CommitErrorNotify;
            }

            // Add to the total units contributed to the pool
            contributionSums.TotalUnitsContributed += quantity;

            byte[] sumsBytes = ContributionSumsToBytes(contributionSums);
            Storage.Put(Storage.CurrentContext, CURRENT_CONTRIBUTIONS_KEY, sumsBytes);
            //Runtime.Notify("putContributionSums", sumsBytes);

            // Save contribution record
            contribution.UnitsContributed = quantity;
            contribution.ContributionHeight = Blockchain.GetHeight();
            contribution.CompoundHeight = contribution.ContributionHeight;
            contribution.FeesCollectedSnapshot = totalFeesCollected;
            contribution.FeeUnitsSnapshot = contributionSums.TotalFeeUnits;

            PutContribution(contribution);
            Runtime.Notify("contributed", sender, contribution.UnitsContributed, contribution.ContributionHeight, contribution.FeesCollectedSnapshot, contribution.FeeUnitsSnapshot);
            return true;
CommitErrorNotify:
            notifyArgs[0] = "commitError";
            Runtime.Notify(notifyArgs);
            return false;
        }

        public static BigInteger GetContributed(byte[] userAddress)
        {
            Contribution contribution = GetContribution(userAddress);
            return contribution.UnitsContributed;
        }

        private static BigInteger GetAvailableToClaim(Contribution contribution, BigInteger totalFeesCollected, ContributionSums contributionSums)
        {
            BigInteger feesCollectedDuringCommitment = totalFeesCollected - contribution.FeesCollectedSnapshot;
            BigInteger feesToClaim;
            object[] notifyArgs;
            if (feesCollectedDuringCommitment <= 0)
            {
                notifyArgs = new object[] { "GetAvailableToClaim() No fees collected" };
                feesToClaim = 0;
                goto ExitGetAvailableToClaim;
            }
            BigInteger contributionFeeUnitWeight = contribution.UnitsContributed * feesCollectedDuringCommitment;
            BigInteger feeUnitsDuringCommitment = contributionSums.TotalFeeUnits - contribution.FeeUnitsSnapshot;
            if (feeUnitsDuringCommitment <= 0)
            {
                notifyArgs = new object[] { "GetAvailableToClaim() No Fee Units available." };
                feesToClaim = 0;
                goto ExitGetAvailableToClaim;
            }
            feesToClaim = feesCollectedDuringCommitment * contributionFeeUnitWeight / feeUnitsDuringCommitment;
            notifyArgs = new object[] { "calcAvailableToClaim", contribution.UserAddress, feesCollectedDuringCommitment,
                contributionFeeUnitWeight, feeUnitsDuringCommitment, feesToClaim };
ExitGetAvailableToClaim:
            Runtime.Notify(notifyArgs);
            return feesToClaim;
        }

        public static BigInteger GetAvailableToClaim(byte[] userAddress)
        {
            Contribution contribution = GetContribution(userAddress);
            if (contribution.UnitsContributed == 0) return 0;

            BigInteger totalFeesCollected = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY).AsBigInteger();
            ContributionSums contributionSums = GetContributionSums();

            return GetAvailableToClaim(contribution, totalFeesCollected, contributionSums);
        }

        private static bool Claim(byte[] userAddress)
        {
            if (userAddress == GetManager())
            {
                Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
                userAddress = ReadWithdrawToAddress(tx);
            }

            Contribution contribution = GetContribution(userAddress);
            if (contribution.UnitsContributed == 0)
            {
                Runtime.Notify("claimFail", "No quantity committed", userAddress);
                return false;
            }

            byte[] poolData = Storage.Get(Storage.CurrentContext, FEES_POOL_KEY);
            BigInteger totalFeesCollected = BigIntegerFrom8Bytes(poolData.Range(0, 8));

            ContributionSums contributionSums = GetContributionSums();

            // Update total fee units if fees have been collected since the last applied fee snapshot
            UpdateTotalFeeUnits(totalFeesCollected, contributionSums);

            BigInteger availableToClaim = GetAvailableToClaim(contribution, totalFeesCollected, contributionSums);

            // Delete their investment
            DeleteContribution(contribution);

            // Reduce the total pool of investments
            contributionSums.TotalUnitsContributed -= contribution.UnitsContributed;

            // Write back the contribution sums.
            Storage.Put(Storage.CurrentContext, CURRENT_CONTRIBUTIONS_KEY, ContributionSumsToBytes(contributionSums));

            BigInteger amountToReturn = contribution.UnitsContributed;
            if (contribution.ContributionHeight + GetClaimMinimumBlocks() <= Blockchain.GetHeight())
            {
                amountToReturn += availableToClaim;
            }
            else
            {
                Runtime.Notify("claimFeesWentToOwner", "Less min blocks since commit, fees awarded to owner.",
                    userAddress, availableToClaim);
                // Add the sum of the amount they would have received back to the contract owner's account
                BigInteger contractOwnerBalance = poolData.Range(8, 8).AsBigInteger();
                contractOwnerBalance += availableToClaim;

                poolData = NormalizeBigIntegerTo8ByteArray(totalFeesCollected)
                    .Concat(NormalizeBigIntegerTo8ByteArray(contractOwnerBalance));
                Storage.Put(Storage.CurrentContext, FEES_POOL_KEY, poolData);
            }

            // Send claimed amount directly back to their wallet
            var transferSucceeded = SendNep5(userAddress, APH, amountToReturn);
            if (transferSucceeded == false)
            {
                Runtime.Notify("claimFail", "sendNep5 Transfer failed", userAddress, APH, amountToReturn);
                return false;
            }

            Runtime.Notify("claimed", userAddress, amountToReturn);
            return true;
        }

        private static bool Compound(byte[] userAddress)
        {
            if (userAddress == GetWhitelister() || userAddress == GetManager())
            {
                Transaction tx = (Transaction)ExecutionEngine.ScriptContainer;
                userAddress = ReadWithdrawToAddress(tx);
            }

            Contribution contribution = GetContribution(userAddress);
            if (contribution.UnitsContributed == 0)
            {
                Runtime.Notify("compoundFail", "No quantity committed", userAddress);
                return false;
            }

            BigInteger minBlocks = GetClaimMinimumBlocks();
            if (contribution.CompoundHeight + minBlocks > Blockchain.GetHeight())
            {
                // Not yet eligible to compound.
                Runtime.Notify("compoundFail", "Not yet eligible to compound", userAddress, contribution.CompoundHeight,
                    minBlocks, Blockchain.GetHeight());
                return false;
            }

            BigInteger totalFeesCollected = BigIntegerFrom8Bytes(
                Storage.Get(Storage.CurrentContext, FEES_POOL_KEY).Range(0, 8));

            ContributionSums contributionSums = GetContributionSums();

            // Update total fee units if fees have been collected since the last applied fee snapshot
            UpdateTotalFeeUnits(totalFeesCollected, contributionSums);

            BigInteger availableToClaim = GetAvailableToClaim(contribution, totalFeesCollected, contributionSums);

            contribution.CompoundHeight = Blockchain.GetHeight();
            contribution.UnitsContributed += availableToClaim;
            contributionSums.TotalUnitsContributed += availableToClaim;
            contribution.FeesCollectedSnapshot = totalFeesCollected;
            contribution.FeeUnitsSnapshot = contributionSums.TotalFeeUnits;

            // Write back the contribution sums.
            Storage.Put(Storage.CurrentContext, CURRENT_CONTRIBUTIONS_KEY, ContributionSumsToBytes(contributionSums));

            PutContribution(contribution);

            Runtime.Notify("compound", userAddress, contribution.UnitsContributed, contribution.CompoundHeight,
                contribution.FeesCollectedSnapshot, contribution.FeeUnitsSnapshot);
            return true;
        }

        private static void PutContribution(Contribution contribution)
        {
            byte[] bytes = ContributionToBytes(contribution);
            PutUserValue(contribution.UserAddress, APH.Concat(POSTFIX_USER_ASSET_CONTRIBUTED), bytes);
            Runtime.Notify("putContribution", contribution.UserAddress, bytes);
        }

        private static Contribution GetContribution(byte[] userAddress)
        {
            byte[] bytes = GetUserValue(userAddress, APH.Concat(POSTFIX_USER_ASSET_CONTRIBUTED));
            if (bytes.Length != 0)
            {
                return ContributionFromBytes(bytes);
            }

            // Runtime.Notify("GetContribution() Contribution not found", userAddress);
            Contribution blank = new Contribution();
            blank.UserAddress = userAddress;
            blank.UnitsContributed = 0;
            return blank;
        }

        private static void DeleteContribution(Contribution contribution)
        {
            DeleteUserValue(contribution.UserAddress, APH.Concat(POSTFIX_USER_ASSET_CONTRIBUTED));
            Runtime.Notify("deleteContribution", contribution.UserAddress);
        }

        private static ContributionSums GetContributionSums()
        {
            byte[] bytes = Storage.Get(Storage.CurrentContext, CURRENT_CONTRIBUTIONS_KEY);
            if (bytes.Length != 0) return ContributionSumsFromBytes(bytes);

            ContributionSums sums = new ContributionSums();

            // Initializing this way makes Runtime.Notify output more uniform when dispalying these values.
            sums.TotalUnitsContributed = EIGHT_BYTES.AsBigInteger();
            sums.LastAppliedFeeSnapshot = SIXTEEN_BYTES.AsBigInteger();
            sums.TotalFeeUnits = SIXTEEN_BYTES.AsBigInteger();
            return sums;
        }

        private static byte[] ContributionSumsToBytes(ContributionSums contributionSums)
        {
            return NormalizeBigIntegerTo8ByteArray(contributionSums.TotalUnitsContributed)
                .Concat(NormalizeBigIntegerTo16ByteArray(contributionSums.LastAppliedFeeSnapshot))
                .Concat(NormalizeBigIntegerTo16ByteArray(contributionSums.TotalFeeUnits));
        }

        private static ContributionSums ContributionSumsFromBytes(byte[] bytes)
        {
            ContributionSums sums = new ContributionSums();
            sums.TotalUnitsContributed = bytes.Range(0, 8).AsBigInteger();
            sums.LastAppliedFeeSnapshot = bytes.Range(8, 16).AsBigInteger();
            sums.TotalFeeUnits = bytes.Range(24, 16).AsBigInteger();
            return sums;
        }

        private static byte[] ContributionToBytes(Contribution contribution)
        {
            byte[] quantityNormalized = NormalizeBigIntegerTo8ByteArray(contribution.UnitsContributed);
            byte[] contributionHeightNormalized = NormalizeBigIntegerTo8ByteArray(contribution.ContributionHeight);
            byte[] compoundHeightNormalized = NormalizeBigIntegerTo8ByteArray(contribution.CompoundHeight);
            byte[] collectedNormalized = NormalizeBigIntegerTo8ByteArray(contribution.FeesCollectedSnapshot);
            byte[] feeUnitsSnapshotNormalized = NormalizeBigIntegerTo16ByteArray(contribution.FeeUnitsSnapshot);
            byte[] bytes = contribution.UserAddress
                .Concat(quantityNormalized)
                .Concat(contributionHeightNormalized)
                .Concat(compoundHeightNormalized)
                .Concat(collectedNormalized)
                .Concat(feeUnitsSnapshotNormalized);

            return bytes;
        }

        private static Contribution ContributionFromBytes(byte[] bytes)
        {
            Contribution contribution = new Contribution();
            contribution.UserAddress = bytes.Range(0, 20);
            contribution.UnitsContributed = bytes.Range(20, 8).AsBigInteger();
            contribution.ContributionHeight = bytes.Range(28, 8).AsBigInteger();
            contribution.CompoundHeight = bytes.Range(36, 8).AsBigInteger();
            contribution.FeesCollectedSnapshot = bytes.Range(44, 8).AsBigInteger();
            contribution.FeeUnitsSnapshot = bytes.Range(52, 16).AsBigInteger();

            return contribution;
        }

    }
}
