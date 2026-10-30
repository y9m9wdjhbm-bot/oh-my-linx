namespace LcPlayerMarket;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length==4&&args[0]=="--verify-one-nearby-shop"&&args[2]=="--out")return OneNearbyShopVerification.RunAsync(args[1],args[3]).GetAwaiter().GetResult();
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)) return 5;
        if(args.Length==2&&args[0]=="--closed-shop-cache-selftest")return ClosedShopCacheVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--phase-io-selftest")return PhaseIoVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--resident-host-selftest")return ResidentNativeHostVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--managed-page-optimization-selftest")return ManagedPageOptimizationVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--v95-integration-selftest")return V95IntegrationSelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--page-interval-ms-selftest")return PageIntervalMillisecondsVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==3 && args[0]=="--debug-api-fixture") return MarketDebugApiV1.RunFixture(args[1],args[2]);
        if(args.Length==6&&args[0]=="--live-automatic-verify"&&args[2]=="--out"&&args[4]=="--page-open-interval-ms")
        {
            if(!int.TryParse(args[5],out int pageInterval)||(pageInterval!=0&&pageInterval<100)||pageInterval>5000)return 2;
            return LiveAutomaticVerification.RunAsync(args[1],args[3],pageInterval).GetAwaiter().GetResult();
        }
        if(args.Length==4&&args[0]=="--live-automatic-verify"&&args[2]=="--out") return LiveAutomaticVerification.RunAsync(args[1],args[3]).GetAwaiter().GetResult();

        if(args.Length==4&&args[0]=="--current-template-readonly")return CurrentTemplateReadOnlyVerification.Run(args[1],args[2],args[3]);
        if(args.Length==3&&args[0]=="--v78-template-compatibility-selftest")return TemplateAdapterVerification.RunCurrent(args[1],args[2]);
        if(args.Length==2&&args[0]=="--capture-auto-recovery-selftest")return PatrolCaptureAutoRecoveryVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--owner-changed-preflight-selftest")return OwnerChangedPreflightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--capture-terminal-selftest")return CaptureTerminalVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--broker-pending-selftest")return BrokerPendingVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--targeted-dispatch-selftest")return TargetedDispatchVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--reset-native-flight-selftest")return ResetNativeFlightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--native-flight-selftest")return NativeHelperFlightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--patrol-startup-mainform-selftest")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return PatrolStartupMainFormVerification.RunReport(args[1]);
        }
        if(args.Length==2&&args[0]=="--patrol-startup-readiness-selftest")return PatrolStartupReadinessVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--patrol-startup-continuous-selftest")return PatrolStartupContinuousVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--continuous-batch-readiness-selftest")return ContinuousBatchReadinessVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--native-readiness-selftest")return ShopNativeReadinessVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--native-guard-rejected-selftest")return NativeGuardRejectedVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--verify-not-entered-pages")return NotEnteredPageVerification.RunReport(args[1]);
        if(args.Length==3&&args[0]=="--native-guard-reconcile")return NativeGuardRejectedReconcile.Run(args[1],args[2]);

        if(args.Length==2&&args[0]=="--continuous-recovery-selftest")return ContinuousRecoveryVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--native-page-range-selftest")return NativePageRangeV2Verification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--native-certified-cold-selftest")return NativeCertifiedColdV17Verification.RunReport(args[1]);

        if(args.Length==2&&args[0]=="--empty-observation-selftest")return EmptyPageObservationVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--item-evidence-consumer-selftest")return ItemEvidenceConsumerVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--item-build-certificate-selftest")return ItemBuildCertificateVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--installed-preflight-selftest")return InstalledPreflightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--packet-copy-preflight-selftest")return V16PacketCopyPreflightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--v16-preflight-selftest")return V16InitialPreflightVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--dispatch-timeout-selftest")return DispatchTimeoutVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--patrol-capture-http-budget-selftest")return PatrolCaptureHttpBudgetSelfTest.RunReportAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--template-adapter-self-test")return TemplateAdapterVerification.Run(args[1]);
        if(args.Length==2&&args[0]=="--page-pacing-self-test")return PagePacingVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0] is "--v54-features-self-test" or "--v55-features-self-test")return MarketV54FeaturesVerification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--shopping-only-selftest")return OnlyShoppingDispatchSelfTest.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--session-lifecycle-self-test")return MarketSessionLifecycleSelfTest.RunReportAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--log-paths-selftest")return MarketLogPathsSelfTest.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--reset-search-ui-self-test")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return MainFormResetSearchSelfTest.Run(args[1]);
        }
        if(args.Length==2&&args[0] == "--patrol-v57-self-test")return PatrolV62Verification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0] is "--recovery-v52-self-test" or "--recovery-v53-self-test" or "--recovery-v54-self-test")return MarketRecoveryV54Verification.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==5&&args[0]=="--import-patrol-checkpoint")return 2;
        if(args.Length==2&&args[0]=="--force-dispatch-self-test")return ForceDispatchVerification.Run(args[1]);
        if(args.Length==2&&args[0]=="--force-dispatch-ui-self-test")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return ForceDispatchUiSelfTest.Run(args[1]);
        }
        if(args.Length==3&&args[0]=="--submit-slice-replay")return SubmitSliceReplaySelfTest.Run(args[1],args[2]);
        if(args.Length==3&&args[0] is "--lock-tool-scan" or "--lock-tool-apply" or "--lock-tool-rollback")return MarketLockToolCommands.Run(args[0],args[1],args[2]);
        if(args.Length==3&&args[0]=="--lock-tool-selftest")return LockToolCoreSelfTest.Run(args[1],args[2]);
        if (args.Length == 4 && args[0] == "--repair-known-lock") return ShopLockRecoveryCommand.Run(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--repair-known-lock") return ShopLockRecoveryCommand.Run(args[1],args[2]);
        if (args.Length == 3 && args[0] == "--startup-recovery-selftest") return StartupRecoverySelfTest.Run(args[1],args[2]);
        if (args.Length == 3 && args[0] == "--preflight-replay-selftest") return PreflightReplaySelfTest.Run(args[1],args[2]);
        if (args.Length == 3 && args[0] == "--patrol-telemetry-chain-selftest") return MarketPatrolTelemetryChainSelfTest.RunAsync(args[1], args[2]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--patrol-start-entrypoint-selftest")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return PatrolStartEntrypointSelfTest.Run(args[1]);
        }
        if (args.Length == 2 && args[0] == "--patrol-start-preparation-selftest") return PatrolStartPreparationSelfTest.Run(args[1]);
        if (args.Length == 2 && args[0] == "--data-root-audit") return MarketDataRootAudit.Run(args[1]);
        if(args.Length==2&&args[0]=="--scan-startup-parallel-selftest")return ScanStartupConcurrencySelfTest.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--faststart-offline-selftest")return FastStartupVerification.RunTests(args[1]);
        if(args.Length==3&&args[0]=="--faststart-store-benchmark")return FastStartupVerification.MeasureStores(args[1],args[2]);
        if(args.Length==2&&args[0]=="--patrol-terminal-selftest")return PatrolRoundsSelfTest.RunTerminalAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--patrol-rounds-selftest")return PatrolRoundsSelfTest.RunAsync(args[1]).GetAwaiter().GetResult();
        if(args.Length==2&&args[0]=="--verify-actor-capacity-contract")return ActorCapacityContractVerification.RunReport(args[1]);
        if(args.Length==2&&args[0]=="--verify-navigation-package")return NavigationPackageVerification.Verify(args[1]);
        if(args.Length==2&&args[0]=="--navigation-startup-smoke")return NavigationPackageVerification.StartupAsync(args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--item-kind-38a7-selftest") return ItemKind38A7Verification.RunReport(args[1]);
        if (args.Length == 2 && args[0] == "--current-item-kind-selftest") return CurrentItemKindRegressionSelfTest.RunReport(args[1]);
        StartupTiming.Mark("main");
        if (args.Length == 4 && args[0] == "--replay-state-evidence") return RecordedStateEvidence.Run(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--archive-search-ledger") return SearchArchiveAudit.Run(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--history-cache-audit") return HistoryCacheAudit.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--startup-benchmark") return StartupBenchmark.Run(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--main-layout-selftest" && args[1] == "--out")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            return MainFormLayoutSelfTest.Run(args[2]);
        }
        if (args.Length == 4 && args[0] == "--verify-one-shop" && args[2] == "--out" && ulong.TryParse(args[1], out var owner))
            return OneShopVerification.RunAsync(owner, args[3]).GetAwaiter().GetResult();
        if (args.Length == 5 && args[0] == "--verify-one-waypoint-force" && args[3] == "--out"
            && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var forceDx)
            && int.TryParse(args[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var forceDy))
            return OneWaypointForceVerification.RunAsync(forceDx, forceDy, args[4]).GetAwaiter().GetResult();
        if (args.Length == 5 && args[0] == "--verify-one-waypoint" && args[3] == "--out"
            && int.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dx)
            && int.TryParse(args[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var dy))
            return OneWaypointVerification.RunAsync(dx, dy, args[4]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--standalone-readonly-smoke" && args[1] == "--out")
            return StandaloneReadOnlySmoke.RunAsync(args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--patrol-readonly-smoke" && args[1] == "--out")
            return PatrolReadOnlySmoke.RunAsync(args[2]).GetAwaiter().GetResult();
        if (args.Length == 3 && args[0] == "--saved-search-ui-selftest" && args[1] == "--out")
            return SavedSearchRecordsRenderSelfTest.Run(args[2]);
        if (args.Length == 3 && args[0] == "--stall-draft-ui-selftest" && args[1] == "--out") return StallDraftUiSelfTest.Run(args[2]);
        if (args.Length == 2 && args[0] == "--patrol-native-stop-selftest") return PatrolAutoResumeSelfTest.RunNativeCodeStopReport(args[1]);
        if (args.Length > 0 && args[0] == "--offline-selftest")
        {
            if (args.Length != 1 && !(args.Length == 3 && args[1] == "--out")) return 2;
            string samplePath = args.Length == 3 ? Path.GetFullPath(args[2]) + ".xlsx"
                : Path.Combine(Path.GetTempPath(), "LC-Market-V82-" + Guid.NewGuid().ToString("N") + ".xlsx");
            var checks = new List<OfflineCheck>();
            void RunSuite(string name, Action run)
            {
                try { run(); }
                catch (Exception error) { checks.Add(new(name, false, error.ToString())); }
            }
            RunSuite("single_shop_empty_baseline_suite", () => checks.AddRange(OneNearbyShopBaselineSelfTest.Run()));
            RunSuite("page_pacing_suite", () => checks.AddRange(ShopPagePacingSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("search_reset_broker_suite", () => checks.AddRange(ResetSearchProgressSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("broker_startup_failure_suite", () => checks.AddRange(BrokerInitializationFailureSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_reset_suite", () => checks.AddRange(PatrolResetSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("restart_coordinator_suite", () => checks.AddRange(SearchRestartCoordinatorSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("page_wait_preferences_suite", () => checks.AddRange(PageWaitPreferencesSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("page_wait_configuration_suite", () => checks.AddRange(ShopPageWaitConfigurationSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("page_wait_backend_bridge_suite", () => checks.AddRange(BackendPageWaitSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("navigation_bundle_hash_suite", () => checks.AddRange(NavigationBundleHashSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("broker_pending_suite", () => checks.AddRange(BrokerPendingVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("targeted_dispatch_suite", () => checks.AddRange(TargetedDispatchVerification.Run()));
            RunSuite("shopping_only_dispatch_suite", () => checks.AddRange(OnlyShoppingDispatchSelfTest.Run()));
            RunSuite("session_lifecycle_suite", () => checks.AddRange(MarketSessionLifecycleSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("log_paths_suite", () => checks.AddRange(MarketLogPathsSelfTest.Run()));
            RunSuite("force_dispatch_suite", () => checks.AddRange(ForceDispatchSelfTest.Run()));
            RunSuite("force_patrol_suite", () => checks.AddRange(ForcePatrolSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("startup_readiness_suite", () => checks.AddRange(StartupReadinessSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_auto_resume_suite", () => checks.AddRange(PatrolAutoResumeSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_native_code_stop_suite", () => checks.AddRange(PatrolAutoResumeSelfTest.RunNativeCodeStopAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_v57_source_classification_suite", () => checks.AddRange(PatrolV62Verification.SourceChecks()));
            RunSuite("startup_trace_suite", () => checks.AddRange(ShopStartupTraceSelfTest.Run()));
            RunSuite("owner_tail_replay_suite", () => checks.AddRange(ShopOwnerTailReplaySelfTest.Run()));
            RunSuite("force_preflight_replay_suite", () => checks.AddRange(ForcePreflightReplaySelfTest.Run()));
            RunSuite("patrol_resume_navigation_suite", () => checks.AddRange(PatrolResumeNavigationSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_foreground_suite", () => checks.AddRange(PatrolForegroundGateSelfTest.Run()));
            RunSuite("search_archive_suite", () => { MemoryHub.Service.SearchedOwnerArchiveSelfTest.Run(); checks.Add(new("search_archive_integrity", true, "Archive integrity cases passed")); });
            RunSuite("search_archive_store_suite", () => checks.AddRange(SearchArchiveAudit.SelfTest()));
            RunSuite("current_item_kind_regression_suite", () => checks.AddRange(CurrentItemKindRegressionSelfTest.Run()));
            RunSuite("item_kind_38a7_suite", () => checks.AddRange(ItemKind38A7Verification.Run()));
            RunSuite("kind_mapping_suite", () => checks.AddRange(KindMappingSelfTest.Run()));
            RunSuite("item_raw_state_suite", () => checks.AddRange(MarketItemStateSelfTest.Run().Checks.Select(c => new OfflineCheck(c.Name,c.Passed,c.Detail))));
            RunSuite("history_cache_suite", () => { HistoryReadCacheSelfTest.Run(); checks.Add(new("history_cache_16_invariants", true, "16 assertions passed")); });
            RunSuite("startup_history_suite", () => checks.AddRange(StartupHistorySelfTest.Run()));
            RunSuite("native_protocol_v13_suite", () => checks.AddRange(NativeProtocolV13SelfTest.Run()));
            RunSuite("refresh_read_retry_suite", () => checks.AddRange(ShopRefreshReadRetrySelfTest.Run()));
            RunSuite("stall_draft_suite", () => checks.AddRange(StallDraftSelfTest.Run()));
            RunSuite("reset_native_helper_flight_suite", () => checks.AddRange(ResetNativeFlightVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("native_helper_flight_suite", () => checks.AddRange(NativeHelperFlightVerification.Run()));
            RunSuite("native_readiness_suite", () => checks.AddRange(ShopNativeReadinessVerification.Run()));
            RunSuite("patrol_startup_continuous_suite", () => checks.AddRange(PatrolStartupContinuousVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("continuous_batch_readiness_suite", () => checks.AddRange(ContinuousBatchReadinessVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("native_certified_cold_v17_suite", () => checks.AddRange(NativeCertifiedColdV17Verification.Run()));
            RunSuite("native_page_range_v2_suite", () => checks.AddRange(NativePageRangeV2Verification.Run()));

            RunSuite("native_guard_rejected_suite", () => checks.AddRange(NativeGuardRejectedVerification.Run()));
            RunSuite("not_entered_page_suite", () => checks.AddRange(NotEnteredPageVerification.Run()));

            RunSuite("continuous_recovery_suite", () => checks.AddRange(ContinuousRecoveryVerification.Run()));
            RunSuite("empty_page_observation_suite", () => checks.AddRange(EmptyPageObservationVerification.Run()));
            RunSuite("item_evidence_consumer_suite", () => checks.AddRange(ItemEvidenceConsumerVerification.Run()));
            RunSuite("item_build_certificate_suite", () => checks.AddRange(ItemBuildCertificateVerification.Run()));
            RunSuite("installed_preflight_suite", () => checks.AddRange(InstalledPreflightVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("owner_changed_preflight_suite", () => checks.AddRange(OwnerChangedPreflightVerification.Run()));
            RunSuite("capture_auto_recovery_suite", () => checks.AddRange(PatrolCaptureAutoRecoveryVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("capture_read_incomplete_suite", () => checks.AddRange(PatrolCaptureReadIncompleteVerification.RunAsync().GetAwaiter().GetResult()));
            RunSuite("v16_initial_preflight_suite", () => checks.AddRange(V16InitialPreflightVerification.Run()));
            RunSuite("packet_copy_preflight_suite", () => checks.AddRange(V16PacketCopyPreflightVerification.Run()));
            RunSuite("elapsed_page_pacing_suite", () => checks.AddRange(PagePacingElapsedSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("actor_capacity_contract_suite", () => checks.AddRange(ActorCapacityContractVerification.Run()));
            RunSuite("base_suite", () => checks.AddRange(OfflineSelfTest.Run().Checks));
            RunSuite("multi_sort_suite", () => checks.AddRange(SortingSelfTest.Run().Checks));
            RunSuite("manual_targets_suite", () => checks.AddRange(ManualTargetsSelfTest.Run()));
            RunSuite("failure_presentation_suite", () => checks.AddRange(FailurePresentationSelfTest.Run()));
            RunSuite("shop_only_navigation_suite", () => checks.AddRange(ShopOnlyNavigationSelfTest.Run()));
            RunSuite("standalone_suite", () => checks.AddRange(StandaloneSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("excel_export_suite", () =>
            {
                var result = ExcelExportSelfTest.Run(samplePath);
                checks.AddRange(result.Checks.Select(c => new OfflineCheck(c.Name, c.Passed, c.Detail)));
            });
            RunSuite("search_overlay_presentation_suite", () => checks.AddRange(SearchOverlayPresentationSelfTest.Run()));
            RunSuite("saved_search_presentation_suite", () => checks.AddRange(SavedSearchPresentationSelfTest.Run()));
            RunSuite("search_history_presentation_suite", () => checks.AddRange(ShopSearchHistoryPresentationSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_navigation_source_suite", () => checks.AddRange(PatrolNavigationClientSelfTest.Run()));
            RunSuite("patrol_capture_http_budget_suite", () => checks.AddRange(PatrolCaptureHttpBudgetSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_fresh_source_gate_suite", () => checks.AddRange(PatrolFreshSourceGateSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_navigation_recovery_suite", () => checks.AddRange(PatrolNavigationRecoverySelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_navigation_dispose_suite", () => checks.AddRange(PatrolNavigationDisposeSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_controller_suite", () => checks.AddRange(PatrolControllerSelfTest.RunAsync().GetAwaiter().GetResult()));
            RunSuite("patrol_route_migration_suite", () => checks.AddRange(PatrolRouteStoreSelfTest.Run()));
            RunSuite("search_overlay_render_suite", () => checks.AddRange(SearchStatusOverlaySelfTest.Run(Path.Combine(Path.GetDirectoryName(samplePath)!, "search-overlay-render-v30"))));
            RunSuite("patrol_overlay_suite", () => checks.AddRange(PatrolOverlayPresentation.SelfTest()));
            var report = new OfflineCheckReport("84.0.0", checks.Count > 0 && checks.All(c => c.Passed),
                checks.Count(c => c.Passed), checks.Count(c => !c.Passed), false, false, false, checks.ToArray());
            string json = System.Text.Json.JsonSerializer.Serialize(report, MemoryHub.HubProtocol.Json);
            Console.WriteLine(json);
            if (args.Length == 3)
            {
                using var file = new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file, new System.Text.UTF8Encoding(false));
                writer.WriteLine(json); writer.Flush(); file.Flush(true);
            }
            return report.Success ? 0 : 1;
        }
        if (args.Length != 0) return 2;
        using var instance = new Mutex(true, "Local\\LC.PlayerMarket.V42.Standalone", out bool ownsInstance);
        if (!ownsInstance)
        {
            MessageBox.Show("LC 玩家商店查詢 V96 API 已在執行，請使用現有視窗。", "LC 玩家商店查詢 V96 API");
            return 0;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.Run(new MainForm()); }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "LC 玩家商店查詢 V96 API 啟動失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { instance.ReleaseMutex(); }
        return 0;
    }
}
