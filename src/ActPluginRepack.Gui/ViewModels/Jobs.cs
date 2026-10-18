namespace ActPluginRepack.Gui.ViewModels;

// 진행 화면에서 도는 작업들. Core 를 백그라운드에서 부르고 단계 상태와 결과를 진행 화면 모델에 적는다.
// 작업 폴더를 바꾸는 단계는 작업 폴더 잠금을 잡는다 (Runner.RunLocked — 다른 창이나 CLI 가 같은 폴더에서 동시에 돌지 않게,
// CLI 와 같은 .actrepack.lock). 관리자 권한으로 다시 실행할 때는 그 프로세스가 잠금을 잡아야 하므로 먼저 놓는다.
internal static class Jobs
{
    // ---- 간편 모드: 확인 -> (필요하면) 만들기 -> 설치 ----

    public static IEnumerable<StepItem> FixSteps() =>
    [
        new(StepKeys.Check, "게임과 플러그인 확인"),
        new(StepKeys.Data, "이번 업데이트 정보 모으기"),
        new(StepKeys.Build, "고친 플러그인 만들기"),
        InstallStep("ACT 에 설치"),
    ];

    // ACT 의 파일을 바꾸는 단계: 도중에 멈추지 않는다 (끝까지 하거나 시작 전) — 도는 동안 [취소]를 누를 수 없다
    private static StepItem InstallStep(string name) => new(StepKeys.Install, name, cancellable: false);

    // 일찍 끝낼 때 남은 단계에 적는 말
    private const string Stopped = "멈춤";

    public static async Task FixAndInstall(RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        // 설치 단계에서 막히지 않게 미리 (15초쯤 걸린 뒤에 실패하면 허탈하므로)
        Install.EnsureClosed();
        vm.MapProgress = stage => stage is Prepare.OpcodeStage or Prepare.CsvStage ? StepKeys.Data : stage == Prepare.DoneStage ? null : StepKeys.Check;

        vm.Start(StepKeys.Check);
        var r = await Runner.RunLocked(vm, ws, ct, () => Prepare.Run(ws, new PrepareOptions { IdaFallback = false })); // IDA 는 고급 모드에서만

        // 확인 / 정보 모으기 단계는 결과에 따라 한 번만 끝낸다 (멈춘 까닭은 그 단계에)
        var dataState = r.Steps.Any(s => s.Status == StepStatus.Warning) ? StepState.Warning : StepState.Done;
        void Checked(StepState check = StepState.Done, string? checkDetail = null, StepState? data = null, string? dataDetail = null)
        {
            vm.Finish(StepKeys.Check, check, checkDetail);
            vm.Finish(StepKeys.Data, data ?? dataState, dataDetail);
        }
        // 여기서 끝낸다: 남은 단계는 멈춤으로
        void Stop(string title, string message)
        {
            vm.SkipRemaining(Stopped);
            vm.SetResult(ResultKind.Warning, title, message);
        }

        // 무엇을 할지는 Core 가 정한다 (FixFlow.Decide) — 여기서는 문구와 단계 표시로.
        // 나아가는 결론(패치 필요 없음 / 고쳐야 함)이면 확인 단계를 먼저 끝내고 ACT 의 상태를 다시 읽는다
        // (ACT 의 플러그인을 통째로 읽어 비교한다 — 화면을 막지 않게)
        StatusReport? status = null;
        if (FixFlow.NeedsStatus(r))
        {
            Checked();
            if (r.Outcome == PrepareOutcome.NotNeeded) vm.Finish(StepKeys.Build, StepState.Skipped, "필요 없음");
            // 고쳐야 한다: [공식 최신본으로 업데이트]로 시작했어도 다시 확인해 보니 고쳐야 하면 제목을 맞춘다
            else vm.Title = Titles.Fix;
            status = await TryCollect(ws);
        }
        var d = FixFlow.Decide(r, status);
        switch (d.Action)
        {
            case FixAction.SignatureDownloadFailed:
                Checked(StepState.Failed, "받지 못함");
                Stop("업데이트 정보를 다 받지 못했습니다", WithNote(r, Friendly.NetworkCause(d.Cause)!, warnings: false));
                return;
            case FixAction.SignatureCheckFailed:
                Checked(StepState.Failed, "확인하지 못함");
                Stop("게임 파일을 확인하지 못했습니다", WithNote(r,
                    "게임 파일을 읽지 못해 플러그인의 핵심 부분(패킷을 받는 부분)이 지금 게임과 맞는지 확인하지 못했습니다. " +
                    $"{Notes.GameFilesBusy} (자세한 원인은 [{Titles.Log}]에 있습니다)", warnings: false));
                return;
            // 패치 날 생길 수 있는 결과라 오류가 아니라 경고로 (기록도 펼치지 않는다)
            case FixAction.HooksUnfixable:
                Checked(StepState.Failed, "자동으로 고치지 못함");
                Stop("지금은 자동으로 고칠 수 없습니다", WithNote(r,
                    "이번 게임 업데이트로 플러그인의 핵심 부분(패킷을 받는 부분)이 바뀌었는데, 자동으로 고치지 못했습니다. " +
                    "공식 플러그인 업데이트를 기다리거나 잠시 후 다시 누르세요.\n" + Notes.ReportToDiscord, warnings: false));
                return;
            case FixAction.OpcodeDownloadFailed:
                Checked(data: StepState.Failed, dataDetail: "받지 못함");
                Stop("업데이트 정보를 다 받지 못했습니다", WithNote(r, Friendly.NetworkCause(d.Cause)!, warnings: false));
                return;
            case FixAction.OpcodeProblem:
                Checked(data: StepState.Failed, dataDetail: "준비하지 못함");
                Stop("업데이트 정보를 준비하지 못했습니다", WithNote(r,
                    $"{d.Problem} (자세한 원인은 [{Titles.Log}]에 있습니다)", warnings: false));
                return;
            case FixAction.OpcodesNotPublished:
                Checked(data: StepState.Failed, dataDetail: "아직 공개되지 않음");
                Stop("아직 이번 업데이트 정보가 공개되지 않았습니다", WithNote(r,
                    "패치에 필요한 정보는 보통 게임 업데이트 후 몇 시간 안에 공개됩니다. 잠시 후 다시 누르세요.", warnings: false));
                return;
            case FixAction.UndecidedGameFiles or FixAction.UndecidedPartial:
                Checked(data: StepState.Warning, dataDetail: "판단할 수 없음");
                Stop("지금은 고칠지 판단할 수 없습니다", WithNote(r,
                    d.Action == FixAction.UndecidedGameFiles
                        ? $"게임 파일을 읽지 못해 확인을 끝내지 못했습니다. {Notes.GameFilesBusy}"
                        : "공개된 업데이트 정보가 아직 일부만 있어서 확인을 끝내지 못했습니다. 보통 몇 시간 안에 채워지니 잠시 후 다시 누르세요."));
                return;
            // ACT 에 더 새 플러그인이 있으면 덮어쓰지 않는다 (고쳐야 할 때는 만들기 전에 알린다) — 실패가 아니라 경고로
            case FixAction.NewerInAct:
                vm.SkipRemaining(Stopped);
                throw new NewerPluginInstalledException(d.Status!.Act!.InstalledVersion ?? "?", d.Status.BasePluginVersion ?? "?");
            case FixAction.InstallOfficial:
            {
                var official = d.Status!;
                string before = $"{official.Act!.InstalledVersion ?? "?"}{(official.PatchInstalled ? ", 이 프로그램이 고친 플러그인" : "")}";
                SkipIfCancelled(vm, StepKeys.Install, ct);
                await InstallOfficial(vm, ws, ct, $"공식 최신본 {official.BasePluginVersion}");
                vm.SetResult(ResultKind.Success, "공식 최신본으로 업데이트했습니다", WithNote(r,
                    $"공식 플러그인 {official.BasePluginVersion} 버전이 지금 게임 버전과 이미 맞아서, 고친 플러그인 대신 공식 최신본을 ACT 에 설치했습니다 (전에 있던 것: {before}).\n" +
                    $"이제 {Notes.LaunchAct}"));
                return;
            }
            case FixAction.NothingToDo:
                vm.Finish(StepKeys.Install, StepState.Skipped, "필요 없음");
                vm.SetResult(ResultKind.Success, Notes.NotNeededTitle, WithNote(r, Notes.NotNeeded));
                return;
        }

        // FixAction.Build: 고친 플러그인을 만든다
        vm.Start(StepKeys.Build);
        await Runner.RunLocked(vm, ws, ct, () => Build.Run(ws, new BuildOptions { CsvOptional = true }));
        vm.Finish(StepKeys.Build);

        // 방금 만든 것과 똑같은 고친 플러그인이 이미 ACT 에 있으면 다시 설치하지 않는다 ([다시 확인하기]로 확인만 한 경우).
        // (설치도 같은 파일이면 아무것도 하지 않지만, 관리자 권한 확인 창을 띄우지 않게 먼저 본다)
        if (FixFlow.AfterBuild(await TryCollect(ws)) == FixInstall.AlreadyInstalled)
        {
            vm.Finish(StepKeys.Install, StepState.Skipped, "이미 설치됨");
            vm.SetResult(ResultKind.Success, "이미 설치되어 있습니다", WithNote(r,
                $"ACT 에는 지금 게임 버전에 맞게 고친 플러그인이 이미 설치되어 있습니다. 따로 할 일 없이 {Notes.LaunchAct}\n" +
                AfterInstallNote));
            OfferRestore(vm);
            return;
        }

        // 만드는 동안 [취소]를 눌렀으면 설치를 시작하지 않는다
        SkipIfCancelled(vm, StepKeys.Install, ct);
        vm.Start(StepKeys.Install);
        await InstallWithElevation(vm, ws, ct);
        vm.Finish(StepKeys.Install);
        vm.SetResult(ResultKind.Success, "설치했습니다", WithNote(r,
            $"이제 {Notes.LaunchAct}\n" + AfterInstallNote));
        OfferRestore(vm);
    }

    private const string AfterInstallNote =
        $"공식 플러그인 업데이트가 나온 뒤 이 프로그램에서 [{Titles.Recheck}]를 누르면 공식 최신본으로 바꿉니다. {Notes.HaerubotUpdate}해도 됩니다.";

    // 결과 설명 끝에 붙이는 참고:
    // · 최신 버전이 아니라 이번 확인에서 뺀 게임 (한쪽 서버를 쉬며 업데이트하지 않은 경우 등)
    // · warnings: 경고나 실패로 끝난 확인 단계가 있음 (결과 자체가 그 실패를 알리는 경우는 false). 공식 플러그인을
    //   받지 못한 이유는 할 일이 있으므로(백신 격리 등) 그대로 보인다.
    private static string WithNote(PrepareResult r, string message, bool warnings = true)
    {
        if (warnings)
        {
            if (r.Steps.FirstOrDefault(s => s.Name == Prepare.PluginStep && s.Status == StepStatus.Failed) is { } plugin)
                message += $"\n\n참고: 공식 플러그인 최신 버전을 받는 중에 문제가 있었습니다.\n{plugin.Message}";
            if (r.Steps.Any(s => s.Status != StepStatus.Ok && !s.Name.StartsWith(Prepare.GameStepPrefix, StringComparison.Ordinal)))
                message += $"\n\n일부 단계에 경고가 있습니다. [{Titles.Log}]에서 확인하세요.";
        }
        if (r.PatchNeeded == true && r.UnconfirmedOpcodes.Count > 0)
            message += "\n\n" + Notes.Unconfirmed;
        if (r.PatchNeeded == true && r.RegeneratedSignatures.Count > 0)
            message += "\n\n플러그인의 핵심 부분(패킷을 받는 부분)을 공개된 수정 없이 이 PC 에서 직접 찾아 고쳤습니다. " +
                       $"게임이 튕기거나 ACT 에 전투 기록이 잡히지 않으면 첫 화면의 [{Titles.Restore}]를 누르고, 공식 플러그인이 갱신된 뒤 다시 누르세요.";
        if (r.Outdated.Count > 0)
            message += $"\n\n참고: {string.Join(", ", r.Outdated.Select(c => c.Region.Label))} 클라이언트는 최신 버전이 아니라 이번 확인에서 제외했습니다. " +
                       "그 서버에서 플레이할 때는 런처로 업데이트한 뒤 다시 누르세요.";
        return message;
    }

    // [취소]를 누른 뒤에는 설치를 시작하지 않는다 — 그 단계는 실패가 아니라 '건너뜀'으로 두고 취소로 끝낸다
    private static void SkipIfCancelled(RunViewModel vm, string key, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested) return;
        vm.Finish(key, StepState.Skipped, "취소함");
        ct.ThrowIfCancellationRequested();
    }

    // ---- 되돌리기 ----

    public static IEnumerable<StepItem> RestoreSteps() => [InstallStep("공식 플러그인을 ACT 에 설치")];

    // 고친 플러그인 대신 공식 플러그인(input\plugin — 마지막 확인 때 받은 최신본)을 설치한다 (공식 최신본 설치와 같다).
    // 설치 직전 상태로 되돌리지 않는다: 게임 패치 뒤라 그 전 것도 고장 난 공식본이고, 여러 번 설치했으면 옛 것으로 내려갈 수 있다.
    public static async Task Restore(RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        if (MaterialState.PluginVersionOf(ws.PluginPath) is not { } version)
        {
            vm.Finish(StepKeys.Install, StepState.Warning, "공식 플러그인 없음");
            vm.SetResult(ResultKind.Warning, "되돌리지 않았습니다",
                $"이 프로그램에 받아 둔 공식 플러그인이 없습니다. {Notes.HaerubotUpdate}하면 공식 플러그인으로 돌아갑니다.");
            return;
        }
        await InstallOfficial(vm, ws, ct, $"공식 플러그인 {version}");
        vm.SetResult(ResultKind.Success, "공식 플러그인으로 되돌렸습니다",
            $"ACT 에 이 프로그램이 고친 플러그인 대신 공식 플러그인 {version} 버전을 설치했습니다. ACT 를 다시 실행하면 적용됩니다.\n" +
            "게임 업데이트 뒤 공식 플러그인이 아직 갱신되지 않았다면 ACT 의 일부 기능이 동작하지 않을 수 있습니다. " +
            $"공식 플러그인이 갱신되면 {Notes.HaerubotUpdate}하거나, 이 프로그램에서 [{Titles.Fix}]를 누르세요.");
    }

    // ---- 고급 모드: 고른 작업만 ----

    // 실행 순서: prepare -> rsv -> build -> install -> hotfix (배포 묶음은 설치를 막지 않게 맨 뒤)
    public static IEnumerable<StepItem> SelectedSteps(AdvancedViewModel o)
    {
        if (o.AnyPrepare) yield return new(StepKeys.Prepare, "입력 데이터 준비 (prepare)");
        if (o.DoRsv) yield return new(StepKeys.Rsv, "ACT 로그에서 RSV 가져오기 (rsv)");
        if (o.DoBuild) yield return new(StepKeys.Build, "플러그인 만들기 (build)");
        if (o.DoInstall) yield return InstallStep(o.InstallOfficial ? "ACT 에 공식 최신본 설치 (install --official)" : "ACT 에 설치 (install)");
        if (o.DoHotfix) yield return new(StepKeys.Hotfix, "고친 플러그인 배포 묶음 (hotfix)");
    }

    public static async Task RunSelected(AdvancedViewModel o, RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        if (o.DoInstall) Install.EnsureClosed();
        string? verdict = null;
        if (o.AnyPrepare)
        {
            vm.Start(StepKeys.Prepare);
            var options = new PrepareOptions
            {
                UpdatePlugin = o.UpdatePlugin,
                Sigcheck = o.Sigcheck,
                Repair = o.Repair,
                Opcodes = o.Opcodes,
                Csv = o.Csv,
                ForceIda = o.ForceIda,
                IdaTarget = o.IdaTarget,
                Overwrite = o.Overwrite,
                Sources = o.SelectedSources,
            };
            var r = await Runner.RunLocked(vm, ws, ct, () => Prepare.Run(ws, options));
            foreach (var s in r.Steps) vm.Log.Add(new LogLine(s.SummaryKind, s.SummaryLine()));
            verdict = r.Verdict;
            // 후크가 깨졌거나(점검 실패 포함) opcode 가 빠졌으면 끝나지 않은 것 — 패치가 필요 없음 / 판단 불가는 고른 작업대로 계속
            bool ok = r.Outcome is not (PrepareOutcome.HooksBroken or PrepareOutcome.OpcodesMissing);
            vm.Finish(StepKeys.Prepare, ok ? StepState.Done : StepState.Warning, r.Verdict);
            if (!ok && (o.DoBuild || o.DoInstall))
            {
                vm.SkipRemaining(Stopped);
                vm.SetResult(ResultKind.Warning, "준비가 끝나지 않아 멈췄습니다",
                    r.SignatureCheckFailed
                        ? $"시그니처 점검이 실패했습니다. 게임 파일을 읽지 못했을 수 있습니다(런처가 업데이트 중 등). [{Titles.Log}]을 확인하세요."
                        : r.Outcome == PrepareOutcome.HooksBroken ? "깨진 패킷 후크가 있습니다. input\\signatures 를 확인하세요."
                        : $"opcode 를 못 구한 클라이언트: {string.Join(", ", r.MissingOpcodes)}");
                return;
            }
        }
        if (o.DoRsv)
        {
            vm.Start(StepKeys.Rsv);
            // ACT 로그 찾기는 작업 폴더 잠금 밖에서 — 로그가 없으면 잠그지 않고 경고로 넘어간다
            var rsv = await Runner.Run(vm.Reporter, ct, () =>
            {
                var files = Rsv.RecentLogs(Rsv.LogFolder(), Rsv.DefaultDays);
                if (files.Count == 0) return null;
                using var _ = ws.Lock();
                return Rsv.Import(ws, files);
            });
            if (rsv == null) vm.Finish(StepKeys.Rsv, StepState.Warning, $"최근 {Rsv.DefaultDays}일 ACT 로그가 없습니다 ({Rsv.LogFolder()})");
            else vm.Finish(StepKeys.Rsv, rsv.Conflicts.Count > 0 ? StepState.Warning : StepState.Done, rsv.Summary);
        }
        if (o.DoBuild)
        {
            vm.Start(StepKeys.Build);
            var b = await Runner.RunLocked(vm, ws, ct, () => Build.Run(ws, new BuildOptions { Csv = o.BuildCsv }));
            vm.Finish(StepKeys.Build, b.Warnings.Count > 0 ? StepState.Warning : StepState.Done, string.Join(" / ", b.Warnings));
        }
        if (o.DoInstall)
        {
            SkipIfCancelled(vm, StepKeys.Install, ct);
            vm.Start(StepKeys.Install);
            await InstallWithElevation(vm, ws, ct, official: o.InstallOfficial, overlay: o.InstallOverlay);
            vm.Finish(StepKeys.Install);
            if (!o.InstallOfficial) OfferRestore(vm);
        }
        else await OfferOfficialUpdate(vm, ws);
        var (hotfixNote, hotfixMade) = o.DoHotfix ? await RunHotfix(vm, ws, ct) : (null, true);
        // 끝까지 했어도 경고나 실패로 끝난 단계가 있으면 성공으로 보이지 않게
        bool clean = vm.Steps.All(s => s.State is not (StepState.Warning or StepState.Failed)) && hotfixMade;
        vm.SetResult(clean ? ResultKind.Success : ResultKind.Warning,
            clean ? "선택한 작업을 마쳤습니다" : "선택한 작업을 마쳤지만 확인할 것이 있습니다",
            (verdict ?? $"자세한 내용은 [{Titles.Log}]을 보세요.") +
            (hotfixNote != null ? $"\n고친 플러그인 배포 묶음: {hotfixNote}" : ""));
    }

    // 고친 플러그인 배포 묶음 (설치 뒤에): 만들지 못해도(패치 번호를 모름 등) 설치는 이미 끝났으므로 작업을 실패로 끝내지 않고
    // 결과에 알린다. 여기서 취소해도 마찬가지 (취소 결과는 "ACT 는 그대로"라고 알리므로 설치한 뒤에는 쓰지 않는다).
    // 돌려주는 것: 결과 설명에 붙일 말 (만든 곳, 또는 만들지 못한 까닭)과 만들었는지
    private static async Task<(string? Note, bool Made)> RunHotfix(RunViewModel vm, Workspace ws, CancellationToken ct)
    {
        const string cancelled = "취소해서 만들지 않았습니다.";
        if (ct.IsCancellationRequested)
        {
            vm.Finish(StepKeys.Hotfix, StepState.Skipped, "취소함");
            return (cancelled, false);
        }
        vm.Start(StepKeys.Hotfix);
        HotfixResult hotfix;
        try
        {
            hotfix = await Runner.RunLocked(vm, ws, ct, () =>
            {
                // 패치 번호를 대응표에서 못 구하면 Core 는 "--tag 로 지정"을 안내하지만 화면에서는 지정할 수 없다
                if (MaterialState.Load(ws) is { } prepared && HotfixPackage.PatchTag(prepared.GameVersions) == null)
                    throw new ToolException("게임 버전에 맞는 패치 번호를 알 수 없어(대응표가 아직 갱신되지 않음) 배포 묶음을 만들지 못했습니다. " +
                                            "명령줄에서 hotfix --tag 7.58 처럼 패치 번호를 직접 지정해 만드세요.");
                return HotfixPackage.Create(ws);
            });
        }
        catch (OperationCanceledException)
        {
            vm.Finish(StepKeys.Hotfix, StepState.Skipped, "취소함");
            return (cancelled, false);
        }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            string error = Errors.Describe(ex);
            if (ex is not ToolException) Errors.WriteLog(ex, "hotfix");
            vm.Log.Add(new LogLine(LogKind.Error, $"[hotfix] {error}"));
            vm.Finish(StepKeys.Hotfix, StepState.Failed, error);
            return (error, false);
        }
        string upload = hotfix.UploadCommand;
        vm.Log.Add(new LogLine(LogKind.Info, "  올리기 (release-notes.md 의 '알려진 문제' 를 확인한 뒤):"));
        vm.Log.Add(new LogLine(LogKind.Info, "  " + upload));
        vm.Finish(StepKeys.Hotfix, hotfix.Warnings.Count > 0 ? StepState.Warning : StepState.Done,
            hotfix.Warnings.Count > 0 ? string.Join(" / ", hotfix.Warnings) : $"{hotfix.Tag} - zip + 릴리스 설명");
        string folder = System.IO.Path.GetDirectoryName(hotfix.ZipPath)!;
        const string copyLabel = "올리기 명령 복사";
        vm.AddAction(copyLabel, Wpf.Ui.Controls.SymbolRegular.Copy24,
            action => RunViewModel.CopyWithFeedback(upload, copyLabel, text => action.Text = text));
        vm.AddAction("배포 묶음 폴더 열기", Wpf.Ui.Controls.SymbolRegular.FolderOpen24, _ =>
        {
            Shell.Open(folder);
            return Task.CompletedTask;
        });
        return ($"{hotfix.ZipPath}\nrelease-notes.md 의 '알려진 문제' 를 확인한 뒤 [{copyLabel}]로 올리면 됩니다.", true);
    }

    // ---- 공통 ----

    // official: 고친 플러그인 대신 input\plugin 의 공식 최신본을 설치
    // overlay=false: OverlayPlugin opcode 는 넣지 않는다 (공식본 설치는 원래 넣지 않음)
    // (잠금은 직접 잡는다 — 관리자 권한으로 다시 실행할 때는 그쪽이 잡도록 먼저 놓아야 한다)
    private static async Task InstallWithElevation(RunViewModel vm, Workspace ws, CancellationToken ct, bool official = false,
                                                   bool overlay = true) =>
        await Runner.Run(vm.Reporter, ct, () =>
        {
            var plan = Install.Plan(ws, overlayResources: overlay ? null : "-", official: official);
            try
            {
                using var _ = ws.Lock();
                Install.Run(ws, plan);
                return 0;
            }
            catch (ElevationRequiredException ex)
            {
                Log.Warn($"{ex.Message} - {ElevatedRunner.RelaunchNote}");
                return Check(ElevatedRunner.Run(AppState.CliPath, "install", plan.ToCliArgs(ws)));
            }
        });

    // input\plugin 의 공식 최신본을 ACT 에 설치하는 설치 단계(StepKeys.Install) (공식 최신본으로 업데이트, 공식 플러그인으로 되돌리기)
    private static async Task InstallOfficial(RunViewModel vm, Workspace ws, CancellationToken ct, string detail)
    {
        vm.Start(StepKeys.Install);
        await InstallWithElevation(vm, ws, ct, official: true);
        vm.Finish(StepKeys.Install, StepState.Done, detail);
    }

    // 고급 모드: 공식 최신본이 이미 게임과 맞을 때 고르면 설치 (낮은 버전을 일부러 두었을 수 있으므로 자동으로 하지 않는다)
    // (상태를 읽지 못하면 버튼만 빼고 — 고른 작업은 이미 끝났다)
    private static async Task OfferOfficialUpdate(RunViewModel vm, Workspace ws)
    {
        if (await TryCollect(ws) is not { OfficialUpdate: true } status) return;
        vm.AddAction($"공식 최신본({status.BasePluginVersion})으로 업데이트", Wpf.Ui.Controls.SymbolRegular.ArrowSync24, _ =>
        {
            vm.ActionNote = null; // 다시 누르면 지난 안내는 지운다
            // 결과를 보는 사이에 바뀌었을 수 있다 (해루봇이 플러그인을 업데이트함 등)
            if (TryCollectNow(ws) is not { OfficialUpdate: true } now)
            {
                vm.ActionNote = "지금은 공식 최신본으로 바꿀 것이 없습니다. [확인]을 누른 뒤 상태를 다시 확인하세요.";
                return Task.CompletedTask;
            }
            string version = now.BasePluginVersion ?? "?";
            // 넘어간 뒤에 실패하면 이 버튼이 있던 결과 화면으로 돌아올 수 없으므로 막히면 여기에 알린다
            Navigator.TryShowRun(Titles.OfficialUpdate, [InstallStep($"공식 최신본 {version} 버전을 ACT 에 설치")],
                async (run, token) =>
                {
                    Install.EnsureClosed();
                    await InstallOfficial(run, ws, token, $"공식 최신본 {version}");
                    run.SetResult(ResultKind.Success, "공식 최신본으로 업데이트했습니다",
                        $"ACT 에 공식 플러그인 {version} 버전을 설치했습니다. 이제 {Notes.LaunchAct}");
                },
                note => vm.ActionNote = note);
            return Task.CompletedTask;
        });
    }

    // 관리자 쪽 CLI 의 종료 코드(ExitCodes) -> 예외. ACT/게임 실행 중은 종료 코드로는 어느 것인지 모르므로 여기서 다시 확인해
    // 알리고, 그새 꺼졌으면 목록 없이 알린다. 그 밖의 실패는 관리자 쪽이 남긴 마지막 오류 문장을 그대로 보인다.
    private static int Check(int code)
    {
        string error = ElevatedRunner.LastError is { Length: > 0 } last ? last : $"관리자 권한으로 진행하다가 실패했습니다. [{Titles.Log}]을 확인하세요.";
        switch (code)
        {
            case ExitCodes.Ok:
                return 0;
            case ExitCodes.AppsRunning:
                Install.EnsureClosed();
                throw new AppsRunningException([]);
            case ExitCodes.NewerPlugin:
                throw new NewerPluginInstalledException(error); // 실패가 아니라 그대로 둠 (경고로)
            default:
                throw new ToolException(error);
        }
    }

    // 고친 플러그인을 설치한 결과 아래에: 문제가 생기면 공식 플러그인으로 (막히면 첫 화면의 버튼처럼 여기에 알린다)
    private static void OfferRestore(RunViewModel vm)
    {
        vm.AddAction(Titles.Restore, Wpf.Ui.Controls.SymbolRegular.ArrowUndo24, _ =>
        {
            vm.ActionNote = null;
            Navigator.TryShowRun(Titles.Restore, RestoreSteps(), Restore, note => vm.ActionNote = note);
            return Task.CompletedTask;
        });
    }

    // 작업이 끝난 뒤 다시 읽는 상태 (읽지 못하면 null — 끝난 작업을 오류로 바꾸지 않는다)
    private static async Task<StatusReport?> TryCollect(Workspace ws) => await Task.Run(() => TryCollectNow(ws));

    private static StatusReport? TryCollectNow(Workspace ws)
    {
        try { return Status.Collect(ws); }
        catch (Exception ex) when (Errors.Recoverable(ex))
        {
            Errors.WriteLog(ex, "gui status");
            return null;
        }
    }
}
