namespace ThinkBase.AI.MPP.Models
{
    /// <summary>
    /// Configuration of the stablecoin half of the Machine Payments Protocol support, bound from
    /// the <c>Mpp:Tempo</c> configuration section. An agent that would rather pay in stablecoin than
    /// with a card transfers the price of the call, as a TIP-20 token, to the deposit address on the
    /// Tempo blockchain and presents the hash of that transaction as its payment credential.
    ///
    /// Only the deposit address has to be configured, as in the Stripe machine payments sample: the
    /// chain, the node and the token are those Tempo publishes for the live or the test network,
    /// chosen by <see cref="MppOptions.ApplyStripeDefaults"/> from the mode of the Stripe key. Make
    /// the deposit address with <c>stripe post /v1/crypto/deposit_addresses -d network=tempo</c> so
    /// that Stripe holds what is paid to it.
    /// </summary>
    public class TempoOptions
    {
        /// <summary>Payment method identifier the challenges of this rail are issued for.</summary>
        public const string MethodName = "tempo";

        /// <summary>Tempo mainnet.</summary>
        public const long MainnetChainId = 4217;

        /// <summary>Moderato, the Tempo test network.</summary>
        public const long TestnetChainId = 42431;

        /// <summary>JSON-RPC endpoint of Tempo mainnet.</summary>
        public const string MainnetRpcUrl = "https://rpc.tempo.xyz";

        /// <summary>JSON-RPC endpoint of Moderato.</summary>
        public const string TestnetRpcUrl = "https://rpc.moderato.tempo.xyz";

        /// <summary>USDC (USDC.e), the token charged for on Tempo mainnet.</summary>
        public const string MainnetTokenAddress = "0x20C000000000000000000000b9537d11c60E8b50";

        /// <summary>pathUSD, the token charged for on Moderato.</summary>
        public const string TestnetTokenAddress = "0x20c0000000000000000000000000000000000000";

        /// <summary>
        /// Base units of the token in one cent. Every TIP-20 token has six decimal places, so a
        /// cent is 10,000 of them and there is no mismatch to configure away.
        /// </summary>
        public const long BaseUnitsPerCent = 10000;

        /// <summary>
        /// Smallest transfer that is asked for, in base units of the token: Stripe cannot record a
        /// payment of less than one cent.
        /// </summary>
        public const long MinimumBaseUnits = BaseUnitsPerCent;

        /// <summary>Address the stablecoin is to be transferred to, this server's deposit address.</summary>
        public string? DepositAddress { get; set; }

        /// <summary>Address of the accepted TIP-20 stablecoin; defaults to the one of the chain.</summary>
        public string? TokenAddress { get; set; }

        /// <summary>Tempo chain the transfer must have been made on; defaults to the one of the Stripe key.</summary>
        public long ChainId { get; set; }

        /// <summary>JSON-RPC endpoint the transfer is verified against; defaults to the one of the chain.</summary>
        public string? RpcUrl { get; set; }

        /// <summary>How long to wait for the JSON-RPC endpoint to answer.</summary>
        public int RpcTimeoutSeconds { get; set; } = 10;

        /// <summary>
        /// How long to wait for the node to know of a transfer that has been presented but that it
        /// has not caught up with yet, before the credential is refused.
        /// </summary>
        public int ReceiptWaitSeconds { get; set; } = 5;

        /// <summary>
        /// How long the hash of a settled transfer is remembered, so that one transfer cannot be
        /// presented again to pay for a later call. It has to outlast the longest challenge by far.
        /// </summary>
        public int TransactionMemoryHours { get; set; } = 168;

        /// <summary>True when a deposit address has been configured, so a stablecoin challenge can be offered.</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(DepositAddress);

        /// <summary>
        /// Fills in the chain, the node and the token with what Tempo publishes for the live or the
        /// test network, so that the deposit address is all that has to be configured.
        /// </summary>
        public void ApplyNetworkDefaults(bool liveMode)
        {
            if (ChainId <= 0)
            {
                ChainId = liveMode ? MainnetChainId : TestnetChainId;
            }

            var mainnet = ChainId == MainnetChainId;

            if (string.IsNullOrWhiteSpace(RpcUrl))
            {
                RpcUrl = mainnet ? MainnetRpcUrl : TestnetRpcUrl;
            }

            if (string.IsNullOrWhiteSpace(TokenAddress))
            {
                TokenAddress = mainnet ? MainnetTokenAddress : TestnetTokenAddress;
            }
        }

        /// <summary>The JSON-RPC timeout as a <see cref="TimeSpan"/>.</summary>
        public TimeSpan RpcTimeout => TimeSpan.FromSeconds(RpcTimeoutSeconds <= 0 ? 10 : RpcTimeoutSeconds);

        /// <summary>The wait for a receipt as a <see cref="TimeSpan"/>.</summary>
        public TimeSpan ReceiptWait => TimeSpan.FromSeconds(ReceiptWaitSeconds < 0 ? 5 : ReceiptWaitSeconds);

        /// <summary>The transfer memory as a <see cref="TimeSpan"/>.</summary>
        public TimeSpan TransactionMemory => TimeSpan.FromHours(TransactionMemoryHours <= 0 ? 168 : TransactionMemoryHours);
    }
}
