/*-
 * Copyright (c) 2020, 2022 Oracle and/or its affiliates. All rights reserved.
 *
 * Licensed under the Universal Permissive License v 1.0 as shown at
 *  https://oss.oracle.com/licenses/upl/
 */

using Microsoft.VisualStudio.TestTools.UnitTesting;

// The test require DynamicData data sources to be evaluated during the test
// execution rather than in the discovery phase because these data sources
// may use data created during ClassInitialize methods.  In addition, without
// this attribute it seems that the test explorer does not discover all
// DynamicData test cases.
[assembly: TestDataSourceDiscovery(TestDataSourceDiscoveryOption.DuringExecution)]

namespace Oracle.NoSQL.SDK.Tests
{
    using System;
    using System.Diagnostics;
    using System.Security.Cryptography;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using static Utils;
    using static TestSchemas;

    public abstract class TestBase
    {
        // TestContext gets set for every test.  It is different from the
        // context passed to ClassInitialize().
        public TestContext TestContext { get; set; }

        public static void ClassInitialize(TestContext staticContext)
        {
            staticContext.WriteLine($"In ClassInitialize, TestName={staticContext.TestName}");
        }

        public static void ClassCleanup()
        {
            Debug.WriteLine("In ClassCleanup");
        }
    }

    public abstract class ConfigTestBase : TestBase
    {
        private protected static TempFileCache TempFileCache { get; } =
            new TempFileCache();

        private protected static readonly KVStoreCredentials
            StoreCredentials =
            new KVStoreCredentials("John", "password".ToCharArray());

        // This is not strictly necessary since the credentials file is not
        // loaded at initialization time, reserved for future.
        private protected static readonly string StoreCredentialsFile =
            TempFileCache.GetTempFile(
                JsonSerializer.Serialize(StoreCredentials));

        private protected static Task<KVStoreCredentials>
            LoadStoreCredentials(CancellationToken cancellationToken) =>
            Task.FromResult(StoreCredentials);

        private protected static readonly KVStoreAuthorizationProvider
            StoreAuthProvider = new KVStoreAuthorizationProvider(
                StoreCredentialsFile);

        private protected static readonly IAMCredentials
            CloudCredentialsBase = new IAMCredentials
            {
                TenantId = "ocid1.tenancy.oc1..tenancy",
                UserId = "ocid1.user.oc1..user",
                Fingerprint = "fingerprint"
            };

        private protected static readonly IAMCredentials CloudCredentials =
            CombineProperties(CloudCredentialsBase, new
            {
                PrivateKeyPEM = "private_key_pem"
            });

        // RSA generates key pair on the first use (export), so we make sure
        // the key pair is generated here.  This ensures that this object can
        // be cloned and compared correctly.
        private protected static readonly RSA CloudPrivateKey = new Func<RSA>(
            () => {
                var rsa = RSA.Create();
                rsa.ExportParameters(true);
                return rsa;
            })();

        private protected const string OCIConfigProfile = "my_profile";

        private protected static readonly string OCIConfigFileWithRegion =
            TempFileCache.GetTempFile(IAM.Utils.GetOCIConfigLines(
                OCIConfigProfile, CloudCredentials,
                Region.US_ASHBURN_1.RegionId));

        private protected static readonly string OCIConfigFile =
            OCIConfigFileWithRegion;

        private protected static Task<IAMCredentials> LoadCloudCredentials(
            CancellationToken cancellationToken) =>
            Task.FromResult(CloudCredentials);

        private protected static readonly IAMAuthorizationProvider
            CloudAuthProviderWithOCIConfigRegion =
            new IAMAuthorizationProvider(OCIConfigFileWithRegion,
                OCIConfigProfile);

        private protected static readonly IAMAuthorizationProvider
            CloudAuthProviderWithCredentials =
                new IAMAuthorizationProvider(CloudCredentials);

        private protected static readonly IAMAuthorizationProvider
            CloudAuthProvider = CloudAuthProviderWithCredentials;

        private protected const string CloudSimEndpoint =
            "http://localhost:8080";

        private protected const string SecureStoreEndpoint =
            "https://myhost:8989";

        public new static void ClassCleanup()
        {
            TestBase.ClassCleanup();
            TempFileCache.Dispose();
        }
    }

    public abstract class ClientTestBase<TTests> : TestBase
    {
        private const string ConfigFileProp = "noSQLConfigFile";
        private const string ServiceTypeProp = "serviceType";
        private const string EndpointProp = "endpoint";

        // ReSharper disable once StaticMemberInGenericType
        internal static NoSQLClient client;

        internal static string Compartment => client.Config.Compartment;

        internal static bool IsOnPrem =>
            client.Config.ServiceType == ServiceType.KVStore;

        internal static bool IsSecureOnPrem =>
            IsOnPrem && client.Config.AuthorizationProvider != null;

        internal static bool IsCloudSim =>
            client.Config.ServiceType == ServiceType.CloudSim;

        internal static bool IsAbsoluteConsistency =>
            client.Config.Consistency == Consistency.Absolute;

        // When testing with cloud service or on-prem with rep-factor > 1
        // we may have failures if we are using eventual consistency and the
        // record is retrieved from the replica.  In this case the tests
        // should only use absolute consistency.  This can be indicated by
        // setting absolute consistency in the initial config.
        internal static bool AllowEventualConsistency =>
            client.Config.Consistency != Consistency.Absolute;

        internal static void CheckOnPrem()
        {
            if (!IsOnPrem)
            {
                Assert.Inconclusive(
                    "This test runs only with on-prem kvstore");
            }
        }

        internal static void CheckNotOnPrem()
        {
            if (IsOnPrem)
            {
                Assert.Inconclusive(
                    "This test does not run with on-prem kvstore");
            }
        }

        public new static void ClassInitialize(TestContext staticContext)
        {
            TestBase.ClassInitialize(staticContext);

            if (staticContext.Properties.Contains(ConfigFileProp))
            {
                var configFile =
                    (string)staticContext.Properties[ConfigFileProp];
                Debug.Assert(configFile != null);
                client = new NoSQLClient(configFile);
            }
            else
            {
                var serviceType = ServiceType.CloudSim;
                if (staticContext.Properties.Contains(ServiceTypeProp))
                {
                    var serviceTypeStr =
                        (string)staticContext.Properties[ServiceTypeProp];
                    Debug.Assert(serviceTypeStr != null);
                    serviceType = Enum.Parse<ServiceType>(serviceTypeStr);
                }

                var endpoint = "localhost:8080";
                if (staticContext.Properties.Contains(EndpointProp))
                {
                    endpoint = (string)staticContext.Properties[EndpointProp];
                    Debug.Assert(endpoint != null);
                }

                client = new NoSQLClient(new NoSQLConfig
                {
                    ServiceType = serviceType,
                    Endpoint = endpoint
                });
            }
        }

        public new static void ClassCleanup()
        {
            client.Dispose();
            TestBase.ClassCleanup();
        }

    }

    public class TablesTestBase<TTests> : ClientTestBase<TTests>
    {
        internal static async Task CreateTableAsync(TableInfo table,
            IndexInfo[] indexes = null)
        {
            await client.ExecuteTableDDLWithCompletionAsync(
                MakeCreateTable(table, true), table.TableLimits);

            if (indexes != null)
            {
                await CreateIndexesAsync(table, indexes);
            }
        }

        internal static async Task CreateIndexAsync(TableInfo table,
            IndexInfo index)
        {
            await client.ExecuteTableDDLWithCompletionAsync(
                MakeCreateIndex(table, index, true));
        }

        internal static async Task CreateIndexesAsync(TableInfo table,
            IndexInfo[] indexes)
        {
            foreach (var index in indexes)
            {
                await CreateIndexAsync(table, index);
            }
        }

        internal static async Task DropTableAsync(TableInfo table)
        {
            await client.ExecuteTableDDLWithCompletionAsync(
                MakeDropTable(table, true));
        }

        internal static void VerifyTableResult(TableResult result,
            TableInfo table, TableState? expectedState = null,
            TableLimits newTableLimits = null, bool ignoreTableLimits = false)
        {
            Assert.IsNotNull(result);
            Assert.AreEqual(table.Name, result.TableName);
            Assert.IsTrue(Enum.IsDefined(typeof(TableState),
                result.TableState));

            if (expectedState.HasValue)
            {
                Assert.AreEqual(expectedState.Value, result.TableState);
            }

            if (IsOnPrem)
            {
                Assert.IsNull(result.TableLimits);
            }
            else if (!ignoreTableLimits)
            {
                var tableLimits = newTableLimits ?? table.TableLimits;
                AssertDeepEqual(tableLimits, result.TableLimits);
            }

            if (result.TableSchema != null)
            {
                try
                {
                    JsonDocument.Parse(result.TableSchema);
                }
                catch (Exception ex)
                {
                    throw new AssertFailedException(
                        "TableResult.TableSchema is not a valid JSON: " +
                        ex.Message, ex);
                }
            }
        }

        internal static void VerifyActiveTable(TableResult result,
            TableInfo table, TableLimits newTableLimits = null) =>
            VerifyTableResult(result, table, TableState.Active,
                newTableLimits);
    }

}
