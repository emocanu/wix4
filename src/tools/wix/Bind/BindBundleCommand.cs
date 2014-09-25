﻿//-------------------------------------------------------------------------------------------------
// <copyright file="BindBundleCommand.cs" company="Outercurve Foundation">
//   Copyright (c) 2004, Outercurve Foundation.
//   This software is released under Microsoft Reciprocal License (MS-RL).
//   The license and further copyright text can be found in the file
//   LICENSE.TXT at the root directory of the distribution.
// </copyright>
//-------------------------------------------------------------------------------------------------

namespace WixToolset.Bind
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Xml;
    using WixToolset.Cab;
    using WixToolset.Data;
    using WixToolset.Data.Rows;
    using WixToolset.Extensibility;

    /// <summary>
    /// Binds a this.bundle.
    /// </summary>
    internal class BindBundleCommand : ICommand
    {
        public CompressionLevel DefaultCompressionLevel { private get; set; }

        public IEnumerable<IBinderExtension> Extensions { private get; set; }

        public BinderFileManagerCore FileManagerCore { private get; set; }

        public IEnumerable<IBinderFileManager> FileManagers { private get; set; }

        public Output Output { private get; set; }

        public string OutputPath { private get; set; }

        public string PdbFile { private get; set; }

        public TableDefinitionCollection TableDefinitions { private get; set; }

        public string TempFilesLocation { private get; set; }

        public WixVariableResolver WixVariableResolver { private get; set; }

        public IEnumerable<FileTransfer> FileTransfers { get; private set; }

        public IEnumerable<string> ContentFilePaths { get; private set; }

        public void Execute()
        {
            this.FileTransfers = Enumerable.Empty<FileTransfer>();
            this.ContentFilePaths = Enumerable.Empty<string>();

            // First look for data we expect to find... Chain, WixGroups, etc.
            Table chainPackageTable = this.Output.Tables["ChainPackage"];
            if (null == chainPackageTable || 0 == chainPackageTable.Rows.Count)
            {
                // We shouldn't really get past the linker phase if there are
                // no group items... that means that there's no UX, no Chain,
                // *and* no Containers!
                throw new WixException(WixErrors.MissingBundleInformation("ChainPackage"));
            }

            Table wixGroupTable = this.Output.Tables["WixGroup"];
            if (null == wixGroupTable || 0 == wixGroupTable.Rows.Count)
            {
                // We shouldn't really get past the linker phase if there are
                // no group items... that means that there's no UX, no Chain,
                // *and* no Containers!
                throw new WixException(WixErrors.MissingBundleInformation("WixGroup"));
            }

            // Ensure there is one and only one row in the WixBundle table.
            // The compiler and linker behavior should have colluded to get
            // this behavior.
            Table bundleTable = this.Output.Tables["WixBundle"];
            if (null == bundleTable || 1 != bundleTable.Rows.Count)
            {
                throw new WixException(WixErrors.MissingBundleInformation("WixBundle"));
            }

            // Ensure there is one and only one row in the WixBootstrapperApplication table.
            // The compiler and linker behavior should have colluded to get
            // this behavior.
            Table baTable = this.Output.Tables["WixBootstrapperApplication"];
            if (null == baTable || 1 != baTable.Rows.Count)
            {
                throw new WixException(WixErrors.MissingBundleInformation("WixBootstrapperApplication"));
            }

            // Ensure there is one and only one row in the WixChain table.
            // The compiler and linker behavior should have colluded to get
            // this behavior.
            Table chainTable = this.Output.Tables["WixChain"];
            if (null == chainTable || 1 != chainTable.Rows.Count)
            {
                throw new WixException(WixErrors.MissingBundleInformation("WixChain"));
            }

            foreach (BinderExtension extension in this.Extensions)
            {
                extension.Initialize(Output);
            }

            if (Messaging.Instance.EncounteredError)
            {
                return;
            }

            // Localize fields, resolve wix variables, and resolve file paths.
            ExtractEmbeddedFiles filesWithEmbeddedFiles = new ExtractEmbeddedFiles();

            ResolveFieldsCommand command = new ResolveFieldsCommand();
            command.Tables = this.Output.Tables;
            command.FilesWithEmbeddedFiles = filesWithEmbeddedFiles;
            command.FileManagerCore = this.FileManagerCore;
            command.FileManagers = this.FileManagers;
            command.SupportDelayedResolution = true;
            command.TempFilesLocation = this.TempFilesLocation;
            command.WixVariableResolver = this.WixVariableResolver;
            command.Execute();

            IEnumerable<DelayedField> delayedFields = command.DelayedFields;

            // If there are any fields to resolve later, create the cache to populate during bind.
            IDictionary<string, string> variableCache = null;
            if (delayedFields.Any())
            {
                variableCache = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
            }

            if (Messaging.Instance.EncounteredError)
            {
                return;
            }

            // TODO: Although the WixSearch tables are defined in the Util extension,
            // the Bundle Binder has to know all about them. We hope to revisit all
            // of this in the 4.0 timeframe.
            Dictionary<string, WixSearchInfo> allSearches = new Dictionary<string, WixSearchInfo>();
            Table wixFileSearchTable = this.Output.Tables["WixFileSearch"];
            if (null != wixFileSearchTable && 0 < wixFileSearchTable.Rows.Count)
            {
                foreach (Row row in wixFileSearchTable.Rows)
                {
                    WixFileSearchInfo fileSearchInfo = new WixFileSearchInfo(row);
                    allSearches.Add(fileSearchInfo.Id, fileSearchInfo);
                }
            }

            Table wixRegistrySearchTable = this.Output.Tables["WixRegistrySearch"];
            if (null != wixRegistrySearchTable && 0 < wixRegistrySearchTable.Rows.Count)
            {
                foreach (Row row in wixRegistrySearchTable.Rows)
                {
                    WixRegistrySearchInfo registrySearchInfo = new WixRegistrySearchInfo(row);
                    allSearches.Add(registrySearchInfo.Id, registrySearchInfo);
                }
            }

            Table wixComponentSearchTable = this.Output.Tables["WixComponentSearch"];
            if (null != wixComponentSearchTable && 0 < wixComponentSearchTable.Rows.Count)
            {
                foreach (Row row in wixComponentSearchTable.Rows)
                {
                    WixComponentSearchInfo componentSearchInfo = new WixComponentSearchInfo(row);
                    allSearches.Add(componentSearchInfo.Id, componentSearchInfo);
                }
            }

            Table wixProductSearchTable = this.Output.Tables["WixProductSearch"];
            if (null != wixProductSearchTable && 0 < wixProductSearchTable.Rows.Count)
            {
                foreach (Row row in wixProductSearchTable.Rows)
                {
                    WixProductSearchInfo productSearchInfo = new WixProductSearchInfo(row);
                    allSearches.Add(productSearchInfo.Id, productSearchInfo);
                }
            }

            // Merge in the variable/condition info and get the canonical ordering for
            // the searches.
            List<WixSearchInfo> orderedSearches = new List<WixSearchInfo>();
            Table wixSearchTable = this.Output.Tables["WixSearch"];
            if (null != wixSearchTable && 0 < wixSearchTable.Rows.Count)
            {
                orderedSearches.Capacity = wixSearchTable.Rows.Count;
                foreach (Row row in wixSearchTable.Rows)
                {
                    WixSearchInfo searchInfo = allSearches[(string)row[0]];
                    searchInfo.AddWixSearchRowInfo(row);
                    orderedSearches.Add(searchInfo);
                }
            }

            // Extract files that come from cabinet files (this does not extract files from merge modules).
            ExtractEmbeddedFilesCommand extractEmbeddedFilesCommand = new ExtractEmbeddedFilesCommand();
            extractEmbeddedFilesCommand.FilesWithEmbeddedFiles = filesWithEmbeddedFiles;
            extractEmbeddedFilesCommand.Execute();

            WixBundleRow bundleInfo = (WixBundleRow)bundleTable.Rows[0];
            bundleInfo.PerMachine = true; // default to per-machine but the first-per user package would flip it.

            // Get update if specified.
            Table bundleUpdateTable = this.Output.Tables["WixBundleUpdate"];
            WixBundleUpdateRow bundleUpdateRow = null;
            if (null != bundleUpdateTable)
            {
                bundleUpdateRow = (WixBundleUpdateRow)bundleUpdateTable.Rows[0];
            }

            // Get update registration if specified.
            Table updateRegistrationTable = this.Output.Tables["WixUpdateRegistration"];
            WixUpdateRegistrationRow updateRegistrationInfo = null;
            if (null != updateRegistrationTable)
            {
                updateRegistrationInfo = (WixUpdateRegistrationRow)updateRegistrationTable.Rows[0];
            }

            // Get the explicit payloads.
            Table payloadTable = this.Output.Tables["Payload"];
            Dictionary<string, PayloadInfoRow> allPayloads = new Dictionary<string, PayloadInfoRow>(payloadTable.Rows.Count);

            Table payloadInfoTable = this.Output.EnsureTable(this.TableDefinitions["PayloadInfo"]);
            foreach (PayloadInfoRow row in payloadInfoTable.Rows)
            {
                allPayloads.Add(row.Id, row);
            }

            RowDictionary<Row> payloadDisplayInformationRows = new RowDictionary<Row>(this.Output.Tables["PayloadDisplayInformation"]);
            foreach (Row row in payloadTable.Rows)
            {
                string id = (string)row[0];

                PayloadInfoRow payloadInfo = null;

                if (allPayloads.ContainsKey(id))
                {
                    payloadInfo = allPayloads[id];
                }
                else
                {
                    allPayloads.Add(id, payloadInfo = (PayloadInfoRow)payloadInfoTable.CreateRow(row.SourceLineNumbers));
                }

                payloadInfo.FillFromPayloadRow(Output, row);

                // Check if there is an override row for the display name or description.
                Row payloadDisplayInformationRow;
                if (payloadDisplayInformationRows.TryGetValue(id, out payloadDisplayInformationRow))
                {
                    if (!String.IsNullOrEmpty(payloadDisplayInformationRow[1] as string))
                    {
                        payloadInfo.ProductName = (string)payloadDisplayInformationRow[1];
                    }

                    if (!String.IsNullOrEmpty(payloadDisplayInformationRow[2] as string))
                    {
                        payloadInfo.Description = (string)payloadDisplayInformationRow[2];
                    }
                }

                if (payloadInfo.Packaging == PackagingType.Unknown)
                {
                    payloadInfo.Packaging = bundleInfo.DefaultPackagingType;
                }
            }

            Dictionary<string, ContainerInfo> containers = new Dictionary<string, ContainerInfo>();
            HashSet<string> payloadsAddedToContainers = new HashSet<string>(StringComparer.Ordinal);

            // Create the list of containers.
            Table containerTable = this.Output.Tables["Container"];
            if (null != containerTable)
            {
                foreach (Row row in containerTable.Rows)
                {
                    ContainerInfo container = new ContainerInfo(row, this.TempFilesLocation);
                    containers.Add(container.Id, container);
                }
            }

            // Create the default attached container for payloads that need to be attached but don't have an explicit container.
            ContainerInfo defaultAttachedContainer = new ContainerInfo("WixAttachedContainer", "bundle-attached.cab", "attached", null, this.TempFilesLocation);
            containers.Add(defaultAttachedContainer.Id, defaultAttachedContainer);

            Row baRow = baTable.Rows[0];
            string baPayloadId = (string)baRow[0];

            // Create lists of which payloads go in each container or are layout only.
            foreach (WixGroupRow row in wixGroupTable.RowsAs<WixGroupRow>())
            {
                if (ComplexReferenceChildType.Payload == row.ChildType)
                {
                    PayloadInfoRow payload = allPayloads[row.ChildId];

                    if (ComplexReferenceParentType.Container == row.ParentType)
                    {
                        ContainerInfo container = containers[row.ParentId];

                        // Make sure the BA DLL is the first payload.
                        if (payload.Id.Equals(baPayloadId))
                        {
                            container.Payloads.Insert(0, payload);
                        }
                        else
                        {
                            container.Payloads.Add(payload);
                        }

                        payload.Container = container.Id;
                        payloadsAddedToContainers.Add(row.ChildId);
                    }
                    else if (ComplexReferenceParentType.Layout == row.ParentType)
                    {
                        payload.LayoutOnly = true;
                    }
                }
            }

            ContainerInfo burnUXContainer;
            containers.TryGetValue(Compiler.BurnUXContainerId, out burnUXContainer);
            List<PayloadInfoRow> uxPayloads = null == burnUXContainer ? null : burnUXContainer.Payloads;

            // If we didn't get any UX payloads, it's an error!
            if (null == uxPayloads || 0 == uxPayloads.Count)
            {
                throw new WixException(WixErrors.MissingBundleInformation("BootstrapperApplication"));
            }

            // Get the chain packages, this may add more payloads.
            Dictionary<string, ChainPackageInfo> allPackages = new Dictionary<string, ChainPackageInfo>();
            Dictionary<string, RollbackBoundaryInfo> allBoundaries = new Dictionary<string, RollbackBoundaryInfo>();
            foreach (Row row in chainPackageTable.Rows)
            {
                Compiler.ChainPackageType type = (Compiler.ChainPackageType)Enum.Parse(typeof(Compiler.ChainPackageType), row[1].ToString(), true);
                if (Compiler.ChainPackageType.RollbackBoundary == type)
                {
                    RollbackBoundaryInfo rollbackBoundary = new RollbackBoundaryInfo(row);
                    allBoundaries.Add(rollbackBoundary.Id, rollbackBoundary);
                }
                else // package
                {
                    Table chainPackageInfoTable = this.Output.EnsureTable(this.TableDefinitions["ChainPackageInfo"]);

                    ChainPackageInfo packageInfo = new ChainPackageInfo(row, wixGroupTable, allPayloads, containers, this.FileManagers.First(), Output, this.TableDefinitions); // TODO: fix these info objects to not take the file managers or any of this and make them just rows.
                    allPackages.Add(packageInfo.Id, packageInfo);

                    chainPackageInfoTable.Rows.Add(packageInfo);

                    // Add package properties to resolve fields later.
                    if (null != variableCache)
                    {
                        BindBundleCommand.PopulatePackageVariableCache(packageInfo, variableCache);
                    }
                }
            }

            // Determine patches to automatically slipstream.
            this.AutomaticallySlipstreamPatches(Output, allPackages.Values);

            // NOTE: All payloads should be generated before here with the exception of specific engine and ux data files.

            List<FileTransfer> fileTransfers = new List<FileTransfer>();
            string layoutDirectory = Path.GetDirectoryName(this.OutputPath);

            // Handle any payloads not explicitly in a container.
            foreach (string payloadName in allPayloads.Keys)
            {
                if (!payloadsAddedToContainers.Contains(payloadName))
                {
                    PayloadInfoRow payload = allPayloads[payloadName];
                    if (PackagingType.Embedded == payload.Packaging)
                    {
                        payload.Container = defaultAttachedContainer.Id;
                        defaultAttachedContainer.Payloads.Add(payload);
                    }
                    else if (!String.IsNullOrEmpty(payload.FullFileName))
                    {
                        FileTransfer transfer;
                        if (FileTransfer.TryCreate(payload.FullFileName, Path.Combine(layoutDirectory, payload.Name), false, "Payload", payload.SourceLineNumbers, out transfer))
                        {
                            fileTransfers.Add(transfer);
                        }
                    }
                }
            }

            // Give the UX payloads their embedded IDs...
            for (int uxPayloadIndex = 0; uxPayloadIndex < uxPayloads.Count; ++uxPayloadIndex)
            {
                PayloadInfoRow payload = uxPayloads[uxPayloadIndex];

                // In theory, UX payloads could be embedded in the UX CAB, external to the
                // bundle EXE, or even downloaded. The current engine requires the UX to be
                // fully present before any downloading starts, so that rules out downloading.
                // Also, the burn engine does not currently copy external UX payloads into
                // the temporary UX directory correctly, so we don't allow external either.
                if (PackagingType.Embedded != payload.Packaging)
                {
                    Messaging.Instance.OnMessage(WixWarnings.UxPayloadsOnlySupportEmbedding(payload.SourceLineNumbers, payload.FullFileName));
                    payload.Packaging = PackagingType.Embedded;
                }

                payload.EmbeddedId = String.Format(CultureInfo.InvariantCulture, BurnCommon.BurnUXContainerEmbeddedIdFormat, uxPayloadIndex);
            }

            if (Messaging.Instance.EncounteredError)
            {
                return;
            }

            // If catalog files exist, non-embedded payloads should validate with the catalogs.
            Table catalogTable = this.Output.Tables["WixCatalog"];
            IEnumerable<WixCatalogRow> catalogs = (null == catalogTable) ? Enumerable.Empty<WixCatalogRow>() : catalogTable.RowsAs<WixCatalogRow>();

            if (catalogs.Any())
            {
                VerifyPayloadsWithCatalogCommand verifyPayloadsWithCatalogCommand = new VerifyPayloadsWithCatalogCommand();
                verifyPayloadsWithCatalogCommand.Catalogs = catalogs;
                verifyPayloadsWithCatalogCommand.Payloads = allPayloads.Values;
            }

            if (Messaging.Instance.EncounteredError)
            {
                return;
            }

            // Process the chain of packages to add them in the correct order
            // and assign the forward rollback boundaries as appropriate. Remember
            // rollback boundaries are authored as elements in the chain which
            // we re-interpret here to add them as attributes on the next available
            // package in the chain. Essentially we mark some packages as being
            // the start of a rollback boundary when installing and repairing.
            // We handle uninstall (aka: backwards) rollback boundaries after
            // we get these install/repair (aka: forward) rollback boundaries
            // defined.
            ChainInfo chain = new ChainInfo(chainTable.Rows[0]); // WixChain table always has one and only row in it.
            RollbackBoundaryInfo previousRollbackBoundary = null;
            foreach (WixGroupRow row in wixGroupTable.RowsAs<WixGroupRow>())
            {
                if (ComplexReferenceParentType.PackageGroup == row.ParentType && "WixChain" == row.ParentId && ComplexReferenceChildType.Package == row.ChildType)
                {
                    ChainPackageInfo packageInfo = null;
                    if (allPackages.TryGetValue(row.ChildId, out packageInfo))
                    {
                        if (null != previousRollbackBoundary)
                        {
                            chain.RollbackBoundaries.Add(previousRollbackBoundary);

                            packageInfo.RollbackBoundary = previousRollbackBoundary;
                            previousRollbackBoundary = null;
                        }

                        chain.Packages.Add(packageInfo);
                    }
                    else // must be a rollback boundary.
                    {
                        // Discard the next rollback boundary if we have a previously defined boundary.
                        RollbackBoundaryInfo nextRollbackBoundary = allBoundaries[row.ChildId];
                        if (null != previousRollbackBoundary)
                        {
                            Messaging.Instance.OnMessage(WixWarnings.DiscardedRollbackBoundary(nextRollbackBoundary.SourceLineNumbers, nextRollbackBoundary.Id));
                        }
                        else
                        {
                            previousRollbackBoundary = nextRollbackBoundary;
                        }
                    }
                }
            }

            if (null != previousRollbackBoundary)
            {
                Messaging.Instance.OnMessage(WixWarnings.DiscardedRollbackBoundary(previousRollbackBoundary.SourceLineNumbers, previousRollbackBoundary.Id));
            }

            // With the forward rollback boundaries assigned, we can now go
            // through the packages with rollback boundaries and assign backward
            // rollback boundaries. Backward rollback boundaries are used when
            // the chain is going "backwards" which (AFAIK) only happens during
            // uninstall.
            //
            // Consider the scenario with three packages: A, B and C. Packages A
            // and C are marked as rollback boundary packages and package B is
            // not. The naive implementation would execute the chain like this
            // (numbers indicate where rollback boundaries would end up):
            //      install:    1 A B 2 C
            //      uninstall:  2 C B 1 A
            //
            // The uninstall chain is wrong, A and B should be grouped together
            // not C and B. The fix is to label packages with a "backwards"
            // rollback boundary used during uninstall. The backwards rollback
            // boundaries are assigned to the package *before* the next rollback
            // boundary. Using our example from above again, I'll mark the
            // backwards rollback boundaries prime (aka: with ').
            //      install:    1 A B 1' 2 C 2'
            //      uninstall:  2' C 2 1' B A 1
            //
            // If the marked boundaries are ignored during install you get the
            // same thing as above (good) and if the non-marked boundaries are
            // ignored during uninstall then A and B are correctly grouped.
            // Here's what it looks like without all the markers:
            //      install:    1 A B 2 C
            //      uninstall:  2 C 1 B A
            // Woot!
            string previousRollbackBoundaryId = null;
            ChainPackageInfo previousPackage = null;
            foreach (ChainPackageInfo package in chain.Packages)
            {
                if (null != package.RollbackBoundary)
                {
                    if (null != previousPackage)
                    {
                        previousPackage.RollbackBoundaryBackwardId = previousRollbackBoundaryId;
                    }

                    previousRollbackBoundaryId = package.RollbackBoundary.Id;
                }

                previousPackage = package;
            }

            if (!String.IsNullOrEmpty(previousRollbackBoundaryId) && null != previousPackage)
            {
                previousPackage.RollbackBoundaryBackwardId = previousRollbackBoundaryId;
            }

            // Give all embedded payloads that don't have an embedded ID yet an embedded ID.
            int payloadIndex = 0;
            foreach (PayloadInfoRow payload in allPayloads.Values)
            {
                Debug.Assert(PackagingType.Unknown != payload.Packaging);

                if (PackagingType.Embedded == payload.Packaging && String.IsNullOrEmpty(payload.EmbeddedId))
                {
                    payload.EmbeddedId = String.Format(CultureInfo.InvariantCulture, BurnCommon.BurnAttachedContainerEmbeddedIdFormat, payloadIndex);
                    ++payloadIndex;
                }
            }

            // Load the MsiProperty information...
            Table msiPropertyTable = this.Output.Tables["MsiProperty"];
            if (null != msiPropertyTable && 0 < msiPropertyTable.Rows.Count)
            {
                foreach (Row row in msiPropertyTable.Rows)
                {
                    MsiPropertyInfo msiProperty = new MsiPropertyInfo(row);

                    ChainPackageInfo package;
                    if (allPackages.TryGetValue(msiProperty.PackageId, out package))
                    {
                        package.MsiProperties.Add(msiProperty);
                    }
                    else
                    {
                        Messaging.Instance.OnMessage(WixErrors.IdentifierNotFound("Package", msiProperty.PackageId));
                    }
                }
            }

            // Load the SlipstreamMsp information...
            Table slipstreamMspTable = this.Output.Tables["SlipstreamMsp"];
            if (null != slipstreamMspTable && 0 < slipstreamMspTable.Rows.Count)
            {
                foreach (Row row in slipstreamMspTable.Rows)
                {
                    string msiPackageId = (string)row[0];
                    string mspPackageId = (string)row[1];

                    if (!allPackages.ContainsKey(mspPackageId))
                    {
                        Messaging.Instance.OnMessage(WixErrors.IdentifierNotFound("Package", mspPackageId));
                        continue;
                    }

                    ChainPackageInfo package;
                    if (!allPackages.TryGetValue(msiPackageId, out package))
                    {
                        Messaging.Instance.OnMessage(WixErrors.IdentifierNotFound("Package", msiPackageId));
                        continue;
                    }

                    package.SlipstreamMsps.Add(mspPackageId);
                }
            }

            // Load the ExitCode information...
            Table exitCodeTable = this.Output.Tables["ExitCode"];
            if (null != exitCodeTable && 0 < exitCodeTable.Rows.Count)
            {
                foreach (Row row in exitCodeTable.Rows)
                {
                    ExitCodeInfo exitCode = new ExitCodeInfo(row);

                    ChainPackageInfo package;
                    if (allPackages.TryGetValue(exitCode.PackageId, out package))
                    {
                        package.ExitCodes.Add(exitCode);
                    }
                    else
                    {
                        Messaging.Instance.OnMessage(WixErrors.IdentifierNotFound("Package", exitCode.PackageId));
                    }
                }
            }

            // Resolve any delayed fields before generating the manifest.
            if (delayedFields.Any())
            {
                ResolveDelayedFieldsCommand resolveDelayedFieldsCommand = new ResolveDelayedFieldsCommand();
                resolveDelayedFieldsCommand.OutputType = this.Output.Type;
                resolveDelayedFieldsCommand.DelayedFields = delayedFields;
                resolveDelayedFieldsCommand.ModularizationGuid = null;
                resolveDelayedFieldsCommand.VariableCache = variableCache;
                resolveDelayedFieldsCommand.Execute();
            }

            // Set the overridable bundle provider key.
            this.SetBundleProviderKey(Output, bundleInfo);

            // Import or generate dependency providers for packages in the manifest.
            this.ProcessDependencyProviders(Output, allPackages);

            // Generate the core-defined BA manifest tables...
            this.GenerateBAManifestPackageTables(Output, chain.Packages);

            this.GenerateBAManifestPayloadTables(Output, chain.Packages, allPayloads);

            foreach (BinderExtension extension in this.Extensions)
            {
                extension.Finish(Output);
            }

            // Start creating the this.bundle.
            this.PopulateBundleInfoFromChain(bundleInfo, chain.Packages);
            this.PopulateChainInfoTables(Output, bundleInfo, chain.Packages);
            this.GenerateBAManifestBundleTables(Output, bundleInfo);

            // Copy the burn.exe to a writable location then mark it to be moved to its
            // final build location.
            string stubPlatform;
            if (Platform.X64 == bundleInfo.Platform) // today, the x64 Burn uses the x86 stub.
            {
                stubPlatform = "x86";
            }
            else
            {
                stubPlatform = bundleInfo.Platform.ToString();
            }
            string wixExeDirectory = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), stubPlatform);
            string stubFile = Path.Combine(wixExeDirectory, "burn.exe");
            string bundleTempPath = Path.Combine(this.TempFilesLocation, Path.GetFileName(this.OutputPath));

            Messaging.Instance.OnMessage(WixVerboses.GeneratingBundle(bundleTempPath, stubFile));
            File.Copy(stubFile, bundleTempPath, true);
            File.SetAttributes(bundleTempPath, FileAttributes.Normal);

            FileTransfer bundleTransfer;
            if (FileTransfer.TryCreate(bundleTempPath, this.OutputPath, true, "Bundle", bundleInfo.SourceLineNumbers, out bundleTransfer))
            {
                bundleTransfer.Built = true;
                fileTransfers.Add(bundleTransfer);
            }

            // Create our manifests, CABs and final EXE...
            string baManifestPath = Path.Combine(this.TempFilesLocation, "bundle-BootstrapperApplicationData.xml");
            this.CreateBootstrapperApplicationManifest(Output, baManifestPath, uxPayloads);

            // Add the bootstrapper application manifest to the set of UX payloads.
            PayloadInfoRow baManifestPayload = PayloadInfoRow.Create(null /*TODO*/, Output, Common.GenerateIdentifier("ux", "BootstrapperApplicationData.xml"),
                "BootstrapperApplicationData.xml", baManifestPath, false, true, null, burnUXContainer.Id, PackagingType.Embedded);
            baManifestPayload.EmbeddedId = string.Format(CultureInfo.InvariantCulture, BurnCommon.BurnUXContainerEmbeddedIdFormat, uxPayloads.Count);
            uxPayloads.Add(baManifestPayload);

            // Create all the containers except the UX container first so the manifest in the UX container can contain all size and hash information.
            foreach (ContainerInfo container in containers.Values)
            {
                if (Compiler.BurnUXContainerId != container.Id && 0 < container.Payloads.Count)
                {
                    this.CreateContainer(container, null);
                }
            }

            string manifestPath = Path.Combine(this.TempFilesLocation, "bundle-manifest.xml");
            this.CreateBurnManifest(this.OutputPath, bundleInfo, bundleUpdateRow, updateRegistrationInfo, manifestPath, this.Output.Tables["RelatedBundle"], this.Output.Tables["Variable"], orderedSearches, allPayloads, chain, containers, catalogs, this.Output.Tables["WixBundleTag"], this.Output.Tables["WixApprovedExeForElevation"]);

            this.UpdateBurnResources(bundleTempPath, this.OutputPath, bundleInfo);

            // update the .wixburn section to point to at the UX and attached container(s) then attach the container(s) if they should be attached.
            using (BurnWriter writer = BurnWriter.Open(bundleTempPath))
            {
                FileInfo burnStubFile = new FileInfo(bundleTempPath);
                writer.InitializeBundleSectionData(burnStubFile.Length, bundleInfo.BundleId);

                // Always create UX container and attach it first
                this.CreateContainer(burnUXContainer, manifestPath);
                writer.AppendContainer(burnUXContainer.TempPath, BurnWriter.Container.UX);

                // Now append all other attached containers
                foreach (ContainerInfo container in containers.Values)
                {
                    if (container.Type == "attached")
                    {
                        // The container was only created if it had payloads.
                        if (Compiler.BurnUXContainerId != container.Id && 0 < container.Payloads.Count)
                        {
                            writer.AppendContainer(container.TempPath, BurnWriter.Container.Attached);
                        }
                    }
                }
            }

            // Output the bundle to a file
            if (null != this.PdbFile)
            {
                Pdb pdb = new Pdb();
                pdb.Output = Output;
                pdb.Save(this.PdbFile);
            }

            // Add detached containers to the list of file transfers.
            foreach (ContainerInfo container in containers.Values)
            {
                if ("detached" == container.Type)
                {
                    FileTransfer transfer;
                    if (FileTransfer.TryCreate(Path.Combine(this.TempFilesLocation, container.Name), Path.Combine(layoutDirectory, container.Name), true, "Container", container.SourceLineNumbers, out transfer))
                    {
                        transfer.Built = true;
                        fileTransfers.Add(transfer);
                    }
                }
            }

            this.FileTransfers = fileTransfers;
            this.ContentFilePaths = allPayloads.Values.Where(p => p.ContentFile).Select(p => p.FullFileName).ToList();
        }

        /// <summary>
        /// Populates the variable cache with specific package properties.
        /// </summary>
        /// <param name="package">The package with properties to cache.</param>
        /// <param name="variableCache">The property cache.</param>
        private static void PopulatePackageVariableCache(ChainPackageInfo package, IDictionary<string, string> variableCache)
        {
            string id = package.Id;

            variableCache.Add(String.Concat("packageDescription.", id), package.Description);
            variableCache.Add(String.Concat("packageLanguage.", id), package.Language);
            variableCache.Add(String.Concat("packageManufacturer.", id), package.Manufacturer);
            variableCache.Add(String.Concat("packageName.", id), package.DisplayName);
            variableCache.Add(String.Concat("packageVersion.", id), package.Version);
        }

        private void AutomaticallySlipstreamPatches(Output bundle, ICollection<ChainPackageInfo> packages)
        {
            List<ChainPackageInfo> msiPackages = new List<ChainPackageInfo>();
            Dictionary<string, List<WixBundlePatchTargetCodeRow>> targetsProductCode = new Dictionary<string, List<WixBundlePatchTargetCodeRow>>();
            Dictionary<string, List<WixBundlePatchTargetCodeRow>> targetsUpgradeCode = new Dictionary<string, List<WixBundlePatchTargetCodeRow>>();

            foreach (ChainPackageInfo package in packages)
            {
                if (Compiler.ChainPackageType.Msi == package.ChainPackageType)
                {
                    // Keep track of all MSI packages.
                    msiPackages.Add(package);
                }
                else if (Compiler.ChainPackageType.Msp == package.ChainPackageType && package.Slipstream)
                {
                    // Index target ProductCodes and UpgradeCodes for slipstreamed MSPs.
                    foreach (WixBundlePatchTargetCodeRow row in package.TargetCodes)
                    {
                        if (row.TargetsProductCode)
                        {
                            List<WixBundlePatchTargetCodeRow> rows;
                            if (!targetsProductCode.TryGetValue(row.TargetCode, out rows))
                            {
                                rows = new List<WixBundlePatchTargetCodeRow>();
                                targetsProductCode.Add(row.TargetCode, rows);
                            }

                            rows.Add(row);
                        }
                        else if (row.TargetsUpgradeCode)
                        {
                            List<WixBundlePatchTargetCodeRow> rows;
                            if (!targetsUpgradeCode.TryGetValue(row.TargetCode, out rows))
                            {
                                rows = new List<WixBundlePatchTargetCodeRow>();
                                targetsUpgradeCode.Add(row.TargetCode, rows);
                            }
                        }
                    }
                }
            }

            Table slipstreamMspTable = bundle.EnsureTable(this.TableDefinitions["SlipstreamMsp"]);
            RowIndexedList<Row> slipstreamMspRows = new RowIndexedList<Row>(slipstreamMspTable);

            // Loop through the MSI and slipstream patches targeting it.
            foreach (ChainPackageInfo msi in msiPackages)
            {
                List<WixBundlePatchTargetCodeRow> rows;
                if (targetsProductCode.TryGetValue(msi.ProductCode, out rows))
                {
                    foreach (WixBundlePatchTargetCodeRow row in rows)
                    {
                        Row slipstreamMspRow = slipstreamMspTable.CreateRow(row.SourceLineNumbers, false);
                        slipstreamMspRow[0] = msi.Id;
                        slipstreamMspRow[1] = row.MspPackageId;

                        if (slipstreamMspRows.TryAdd(slipstreamMspRow))
                        {
                            slipstreamMspTable.Rows.Add(slipstreamMspRow);
                        }
                    }

                    rows = null;
                }

                if (!String.IsNullOrEmpty(msi.UpgradeCode) && targetsUpgradeCode.TryGetValue(msi.UpgradeCode, out rows))
                {
                    foreach (WixBundlePatchTargetCodeRow row in rows)
                    {
                        Row slipstreamMspRow = slipstreamMspTable.CreateRow(row.SourceLineNumbers, false);
                        slipstreamMspRow[0] = msi.Id;
                        slipstreamMspRow[1] = row.MspPackageId;

                        if (slipstreamMspRows.TryAdd(slipstreamMspRow))
                        {
                            slipstreamMspTable.Rows.Add(slipstreamMspRow);
                        }
                    }

                    rows = null;
                }
            }
        }

        private void GenerateBAManifestPackageTables(Output bundle, List<ChainPackageInfo> chainPackages)
        {
            Table wixPackagePropertiesTable = bundle.EnsureTable(this.TableDefinitions["WixPackageProperties"]);

            foreach (ChainPackageInfo package in chainPackages)
            {
                Row row = wixPackagePropertiesTable.CreateRow(package.SourceLineNumbers);
                row[0] = package.Id;
                row[1] = package.Vital ? "yes" : "no";
                row[2] = package.DisplayName;
                row[3] = package.Description;
                row[4] = package.Size.ToString(CultureInfo.InvariantCulture); // TODO: DownloadSize (compressed) (what does this mean when it's embedded?)
                row[5] = package.Size.ToString(CultureInfo.InvariantCulture); // Package.Size (uncompressed)
                row[6] = package.InstallSize.ToString(CultureInfo.InvariantCulture); // InstallSize (required disk space)
                row[7] = package.ChainPackageType.ToString(CultureInfo.InvariantCulture);
                row[8] = package.Permanent ? "yes" : "no";
                row[9] = package.LogPathVariable;
                row[10] = package.RollbackLogPathVariable;
                row[11] = (PackagingType.Embedded == package.PackagePayload.Packaging) ? "yes" : "no";
                row[12] = package.DisplayInternalUI ? "yes" : "no";
                if (!String.IsNullOrEmpty(package.ProductCode))
                {
                    row[13] = package.ProductCode;
                }
                if (!String.IsNullOrEmpty(package.UpgradeCode))
                {
                    row[14] = package.UpgradeCode;
                }
                if (!String.IsNullOrEmpty(package.Version))
                {
                    row[15] = package.Version;
                }
                if (!String.IsNullOrEmpty(package.InstallCondition))
                {
                    row[16] = package.InstallCondition;
                }
                switch (package.Cache)
                {
                    case YesNoAlwaysType.No:
                        row[17] = "no";
                        break;
                    case YesNoAlwaysType.Yes:
                        row[17] = "yes";
                        break;
                    case YesNoAlwaysType.Always:
                        row[17] = "always";
                        break;
                }

                Table wixPackageFeatureInfoTable = bundle.EnsureTable(this.TableDefinitions["WixPackageFeatureInfo"]);

                foreach (MsiFeature feature in package.MsiFeatures)
                {
                    Row packageFeatureInfoRow = wixPackageFeatureInfoTable.CreateRow(package.SourceLineNumbers);
                    packageFeatureInfoRow[0] = package.Id;
                    packageFeatureInfoRow[1] = feature.Name;
                    packageFeatureInfoRow[2] = Convert.ToString(feature.Size, CultureInfo.InvariantCulture);
                    packageFeatureInfoRow[3] = feature.Parent;
                    packageFeatureInfoRow[4] = feature.Title;
                    packageFeatureInfoRow[5] = feature.Description;
                    packageFeatureInfoRow[6] = Convert.ToString(feature.Display, CultureInfo.InvariantCulture);
                    packageFeatureInfoRow[7] = Convert.ToString(feature.Level, CultureInfo.InvariantCulture);
                    packageFeatureInfoRow[8] = feature.Directory;
                    packageFeatureInfoRow[9] = Convert.ToString(feature.Attributes, CultureInfo.InvariantCulture);
                }
            }
        }

        private void GenerateBAManifestPayloadTables(Output bundle, List<ChainPackageInfo> chainPackages, Dictionary<string, PayloadInfoRow> payloads)
        {
            Table wixPayloadPropertiesTable = bundle.EnsureTable(this.TableDefinitions["WixPayloadProperties"]);

            foreach (ChainPackageInfo package in chainPackages)
            {
                PayloadInfoRow packagePayload = payloads[package.Payload];

                Row payloadRow = wixPayloadPropertiesTable.CreateRow(packagePayload.SourceLineNumbers);
                payloadRow[0] = packagePayload.Id;
                payloadRow[1] = package.Id;
                payloadRow[2] = packagePayload.Container;
                payloadRow[3] = packagePayload.Name;
                payloadRow[4] = packagePayload.FileSize.ToString();
                payloadRow[5] = packagePayload.DownloadUrl;
                payloadRow[6] = packagePayload.LayoutOnly ? "yes" : "no";

                foreach (PayloadInfoRow childPayload in package.Payloads)
                {
                    payloadRow = wixPayloadPropertiesTable.CreateRow(childPayload.SourceLineNumbers);
                    payloadRow[0] = childPayload.Id;
                    payloadRow[1] = package.Id;
                    payloadRow[2] = childPayload.Container;
                    payloadRow[3] = childPayload.Name;
                    payloadRow[4] = childPayload.FileSize.ToString();
                    payloadRow[5] = childPayload.DownloadUrl;
                    payloadRow[6] = childPayload.LayoutOnly ? "yes" : "no";
                }
            }

            foreach (PayloadInfoRow payload in payloads.Values)
            {
                if (payload.LayoutOnly)
                {
                    Row row = wixPayloadPropertiesTable.CreateRow(payload.SourceLineNumbers);
                    row[0] = payload.Id;
                    row[1] = null;
                    row[2] = payload.Container;
                    row[3] = payload.Name;
                    row[4] = payload.FileSize.ToString();
                    row[5] = payload.DownloadUrl;
                    row[6] = payload.LayoutOnly ? "yes" : "no";
                }
            }
        }

        private void CreateContainer(ContainerInfo container, string manifestFile)
        {
            int payloadCount = container.Payloads.Count; // The number of embedded payloads
            if (!String.IsNullOrEmpty(manifestFile))
            {
                ++payloadCount;
            }

            using (WixCreateCab cab = new WixCreateCab(Path.GetFileName(container.TempPath), Path.GetDirectoryName(container.TempPath), payloadCount, 0, 0, this.DefaultCompressionLevel))
            {
                // If a manifest was provided always add it as "payload 0" to the container.
                if (!String.IsNullOrEmpty(manifestFile))
                {
                    cab.AddFile(manifestFile, "0");
                }

                foreach (PayloadInfoRow payload in container.Payloads)
                {
                    Debug.Assert(PackagingType.Embedded == payload.Packaging);
                    Messaging.Instance.OnMessage(WixVerboses.LoadingPayload(payload.FullFileName));
                    cab.AddFile(payload.FullFileName, payload.EmbeddedId);
                }

                cab.Complete();
            }
        }

        private void PopulateBundleInfoFromChain(WixBundleRow bundleInfo, List<ChainPackageInfo> chainPackages)
        {
            foreach (ChainPackageInfo package in chainPackages)
            {
                if (bundleInfo.PerMachine && YesNoDefaultType.No == package.PerMachine)
                {
                    Messaging.Instance.OnMessage(WixVerboses.SwitchingToPerUserPackage(package.PackagePayload.FullFileName));
                    bundleInfo.PerMachine = false;
                }
            }
        }

        private void PopulateChainInfoTables(Output bundle, WixBundleRow bundleInfo, List<ChainPackageInfo> chainPackages)
        {
            bool hasPerMachineNonPermanentPackages = false;

            foreach (ChainPackageInfo package in chainPackages)
            {
                // Update package scope from bundle scope if default.
                if (YesNoDefaultType.Default == package.PerMachine)
                {
                    package.PerMachine = bundleInfo.PerMachine ? YesNoDefaultType.Yes : YesNoDefaultType.No;
                }

                // Keep track if any per-machine non-permanent packages exist.
                if (YesNoDefaultType.Yes == package.PerMachine && 0 < package.Provides.Count && !package.Permanent)
                {
                    hasPerMachineNonPermanentPackages = true;
                }

                switch (package.ChainPackageType)
                {
                    case Compiler.ChainPackageType.Msi:
                        Table chainMsiPackageTable = bundle.EnsureTable(this.TableDefinitions["ChainMsiPackage"]);
                        ChainMsiPackageRow row = (ChainMsiPackageRow)chainMsiPackageTable.CreateRow(null);
                        row.ChainPackage = package.Id;
                        row.ProductCode = package.ProductCode;
                        row.ProductLanguage = Convert.ToInt32(package.Language, CultureInfo.InvariantCulture);
                        row.ProductName = package.DisplayName;
                        row.ProductVersion = package.Version;
                        if (!String.IsNullOrEmpty(package.UpgradeCode))
                        {
                            row.UpgradeCode = package.UpgradeCode;
                        }
                        break;
                    default:
                        break;
                }
            }

            // We will only register packages in the same scope as the bundle.
            // Warn if any packages with providers are in a different scope
            // and not permanent (permanents typically don't need a ref-count).
            if (!bundleInfo.PerMachine && hasPerMachineNonPermanentPackages)
            {
                Messaging.Instance.OnMessage(WixWarnings.NoPerMachineDependencies());
            }
        }

        private void GenerateBAManifestBundleTables(Output bundle, WixBundleRow bundleInfo)
        {
            Table wixBundlePropertiesTable = bundle.EnsureTable(this.TableDefinitions["WixBundleProperties"]);
            Row row = wixBundlePropertiesTable.CreateRow(bundleInfo.SourceLineNumbers);
            row[0] = bundleInfo.Name;
            row[1] = bundleInfo.LogPathVariable;
            row[2] = (YesNoDefaultType.Yes == bundleInfo.Compressed) ? "yes" : "no";
            row[3] = bundleInfo.BundleId.ToString("B");
            row[4] = bundleInfo.UpgradeCode;
            row[5] = bundleInfo.PerMachine ? "yes" : "no";
        }

        private void CreateBootstrapperApplicationManifest(Output bundle, string path, List<PayloadInfoRow> uxPayloads)
        {
            using (XmlTextWriter writer = new XmlTextWriter(path, Encoding.Unicode))
            {
                writer.Formatting = Formatting.Indented;
                writer.WriteStartDocument();
                writer.WriteStartElement("BootstrapperApplicationData", "http://wixtoolset.org/schemas/v4/2010/BootstrapperApplicationData");

                foreach (Table table in bundle.Tables)
                {
                    if (table.Definition.BootstrapperApplicationData && null != table.Rows && 0 < table.Rows.Count)
                    {
                        // We simply assert that the table (and field) name is valid, because
                        // this is up to the extension developer to get right. An author will
                        // only affect the attribute value, and that will get properly escaped.
#if DEBUG
                        Debug.Assert(Common.IsIdentifier(table.Name));
                        foreach (ColumnDefinition column in table.Definition.Columns)
                        {
                            Debug.Assert(Common.IsIdentifier(column.Name));
                        }
#endif // DEBUG

                        foreach (Row row in table.Rows)
                        {
                            writer.WriteStartElement(table.Name);

                            foreach (Field field in row.Fields)
                            {
                                if (null != field.Data)
                                {
                                    writer.WriteAttributeString(field.Column.Name, field.Data.ToString());
                                }
                            }

                            writer.WriteEndElement();
                        }
                    }
                }

                writer.WriteEndElement();
                writer.WriteEndDocument();
            }
        }

        private void UpdateBurnResources(string bundleTempPath, string outputPath, WixBundleRow bundleInfo)
        {
            WixToolset.Dtf.Resources.ResourceCollection resources = new WixToolset.Dtf.Resources.ResourceCollection();
            WixToolset.Dtf.Resources.VersionResource version = new WixToolset.Dtf.Resources.VersionResource("#1", 1033);

            version.Load(bundleTempPath);
            resources.Add(version);

            // Ensure the bundle info provides a full four part version.
            Version fourPartVersion = new Version(bundleInfo.Version);
            int major = (fourPartVersion.Major < 0) ? 0 : fourPartVersion.Major;
            int minor = (fourPartVersion.Minor < 0) ? 0 : fourPartVersion.Minor;
            int build = (fourPartVersion.Build < 0) ? 0 : fourPartVersion.Build;
            int revision = (fourPartVersion.Revision < 0) ? 0 : fourPartVersion.Revision;

            if (UInt16.MaxValue < major || UInt16.MaxValue < minor || UInt16.MaxValue < build || UInt16.MaxValue < revision)
            {
                throw new WixException(WixErrors.InvalidModuleOrBundleVersion(bundleInfo.SourceLineNumbers, "Bundle", bundleInfo.Version));
            }

            fourPartVersion = new Version(major, minor, build, revision);
            version.FileVersion = fourPartVersion;
            version.ProductVersion = fourPartVersion;

            WixToolset.Dtf.Resources.VersionStringTable strings = version[1033];
            strings["LegalCopyright"] = bundleInfo.Copyright;
            strings["OriginalFilename"] = Path.GetFileName(outputPath);
            strings["FileVersion"] = bundleInfo.Version;    // string versions do not have to be four parts.
            strings["ProductVersion"] = bundleInfo.Version; // string versions do not have to be four parts.

            if (!String.IsNullOrEmpty(bundleInfo.Name))
            {
                strings["ProductName"] = bundleInfo.Name;
                strings["FileDescription"] = bundleInfo.Name;
            }

            if (!String.IsNullOrEmpty(bundleInfo.Publisher))
            {
                strings["CompanyName"] = bundleInfo.Publisher;
            }
            else
            {
                strings["CompanyName"] = String.Empty;
            }

            if (!String.IsNullOrEmpty(bundleInfo.IconPath))
            {
                Dtf.Resources.GroupIconResource iconGroup = new Dtf.Resources.GroupIconResource("#1", 1033);
                iconGroup.ReadFromFile(bundleInfo.IconPath);
                resources.Add(iconGroup);

                foreach (Dtf.Resources.Resource icon in iconGroup.Icons)
                {
                    resources.Add(icon);
                }
            }

            if (!String.IsNullOrEmpty(bundleInfo.SplashScreenBitmapPath))
            {
                Dtf.Resources.BitmapResource bitmap = new Dtf.Resources.BitmapResource("#1", 1033);
                bitmap.ReadFromFile(bundleInfo.SplashScreenBitmapPath);
                resources.Add(bitmap);
            }

            resources.Save(bundleTempPath);
        }

        private void CreateBurnManifest(string outputPath, WixBundleRow bundleInfo, WixBundleUpdateRow updateRow, WixUpdateRegistrationRow updateRegistrationInfo, string path, Table relatedBundlesTable, Table variableTable, List<WixSearchInfo> orderedSearches, Dictionary<string, PayloadInfoRow> allPayloads, ChainInfo chain, Dictionary<string, ContainerInfo> containers, IEnumerable<WixCatalogRow> catalogs, Table wixBundleTagTable, Table wixApprovedExeForElevationTable)
        {
            // For the related bundles with duplicated identifiers the second instance is ignored (i.e. the Duplicates
            // enumeration in the index row list is not used).
            RowIndexedList<RelatedBundleRow> relatedBundles = new RowIndexedList<RelatedBundleRow>(relatedBundlesTable);

            CreateBurnManifestCommand command = new CreateBurnManifestCommand();
            command.FileManagers = this.FileManagers;
            command.ExecutableName = Path.GetFileName(outputPath);
            command.BundleInfo = bundleInfo;
            command.UpdateRow = updateRow;
            command.UpdateRegistrationInfo = updateRegistrationInfo;
            command.OutputPath = path;
            command.RelatedBundles = relatedBundles;
            command.Variables = variableTable.RowsAs<VariableRow>();
            command.OrderedSearches = orderedSearches;
            command.Payloads = allPayloads;
            command.Chain = chain;
            command.Containers = containers;
            command.Catalogs = catalogs;
            command.BundleTags = (null == wixBundleTagTable) ? Enumerable.Empty<Row>() : wixBundleTagTable.Rows;
            command.ApprovedExesForElevation = (null == wixApprovedExeForElevationTable) ? Enumerable.Empty<WixApprovedExeForElevationRow>() : wixApprovedExeForElevationTable.Rows.Cast<WixApprovedExeForElevationRow>();
            command.Execute();
        }

        #region DependencyExtension
        /// <summary>
        /// Imports authored dependency providers for each package in the manifest,
        /// and generates dependency providers for certain package types that do not
        /// have a provider defined.
        /// </summary>
        /// <param name="bundle">The <see cref="Output"/> object for the bundle.</param>
        /// <param name="packages">An indexed collection of chained packages.</param>
        private void ProcessDependencyProviders(Output bundle, Dictionary<string, ChainPackageInfo> packages)
        {
            // First import any authored dependencies. These may merge with imported provides from MSI packages.
            Table wixDependencyProviderTable = bundle.Tables["WixDependencyProvider"];
            if (null != wixDependencyProviderTable && 0 < wixDependencyProviderTable.Rows.Count)
            {
                // Add package information for each dependency provider authored into the manifest.
                foreach (Row wixDependencyProviderRow in wixDependencyProviderTable.Rows)
                {
                    string packageId = (string)wixDependencyProviderRow[1];

                    ChainPackageInfo package = null;
                    if (packages.TryGetValue(packageId, out package))
                    {
                        ProvidesDependency dependency = new ProvidesDependency(wixDependencyProviderRow);

                        if (String.IsNullOrEmpty(dependency.Key))
                        {
                            switch (package.ChainPackageType)
                            {
                                // The WixDependencyExtension allows an empty Key for MSIs and MSPs.
                                case Compiler.ChainPackageType.Msi:
                                    dependency.Key = package.ProductCode;
                                    break;
                                case Compiler.ChainPackageType.Msp:
                                    dependency.Key = package.PatchCode;
                                    break;
                            }
                        }

                        if (String.IsNullOrEmpty(dependency.Version))
                        {
                            dependency.Version = package.Version;
                        }

                        // If the version is still missing, a version could not be harvested from the package and was not authored.
                        if (String.IsNullOrEmpty(dependency.Version))
                        {
                            Messaging.Instance.OnMessage(WixErrors.MissingDependencyVersion(package.Id));
                        }

                        if (String.IsNullOrEmpty(dependency.DisplayName))
                        {
                            dependency.DisplayName = package.DisplayName;
                        }

                        if (!package.Provides.Merge(dependency))
                        {
                            Messaging.Instance.OnMessage(WixErrors.DuplicateProviderDependencyKey(dependency.Key, package.Id));
                        }
                    }
                }
            }

            // Generate providers for MSI packages that still do not have providers.
            foreach (ChainPackageInfo package in packages.Values)
            {
                if (Compiler.ChainPackageType.Msi == package.ChainPackageType && 0 == package.Provides.Count)
                {
                    ProvidesDependency dependency = new ProvidesDependency(package.ProductCode, package.Version, package.DisplayName, 0);

                    if (!package.Provides.Merge(dependency))
                    {
                        Messaging.Instance.OnMessage(WixErrors.DuplicateProviderDependencyKey(dependency.Key, package.Id));
                    }
                }
                else if (Compiler.ChainPackageType.Msp == package.ChainPackageType && 0 == package.Provides.Count)
                {
                    ProvidesDependency dependency = new ProvidesDependency(package.PatchCode, package.Version, package.DisplayName, 0);

                    if (!package.Provides.Merge(dependency))
                    {
                        Messaging.Instance.OnMessage(WixErrors.DuplicateProviderDependencyKey(dependency.Key, package.Id));
                    }
                }
            }
        }

        /// <summary>
        /// Sets the provider key for the bundle.
        /// </summary>
        /// <param name="bundle">The <see cref="Output"/> object for the bundle.</param>
        /// <param name="bundleInfo">The <see cref="BundleInfo"/> containing the provider key and other information for the bundle.</param>
        private void SetBundleProviderKey(Output bundle, WixBundleRow bundleInfo)
        {
            // From DependencyCommon.cs in the WixDependencyExtension.
            const int ProvidesAttributesBundle = 0x10000;

            Table wixDependencyProviderTable = bundle.Tables["WixDependencyProvider"];
            if (null != wixDependencyProviderTable && 0 < wixDependencyProviderTable.Rows.Count)
            {
                // Search the WixDependencyProvider table for the single bundle provider key.
                foreach (Row wixDependencyProviderRow in wixDependencyProviderTable.Rows)
                {
                    object attributes = wixDependencyProviderRow[5];
                    if (null != attributes && 0 != (ProvidesAttributesBundle & (int)attributes))
                    {
                        bundleInfo.ProviderKey = (string)wixDependencyProviderRow[2];
                        break;
                    }
                }
            }

            // Defaults to the bundle ID as the provider key.
        }
        #endregion
    }
}
