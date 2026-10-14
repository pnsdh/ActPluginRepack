namespace ActPluginRepack.Gui.ViewModels;

// 진행 화면에서 도는 작업들. Core 를 백그라운드에서 부르고 단계 상태와 결과를 진행 화면 모델에 적는다.
// 작업 폴더를 바꾸는 단계는 작업 폴더 잠금을 잡는다 (다른 창이나 CLI 가 같은 폴더에서 동시에 돌지 않게 — CLI 와 같은
// .actrepack.lock). 관리자 권한으로 다시 실행할 때는 그 프로세스가 잠금을 잡아야 하므로 먼저 놓는다.
internal static class Jobs
{
    // ---- 간편 모드: 확인 -> (필요하면) 만들기 -> 설치 ----

    public static IEnumerable<StepItem> FixSteps() =>
    [
        new("check", "게임과 플러그인 확인"),
        new("data", "이번 업데이트 정보 모으기"),
        new("build", "고친 플러그인 만들기"),
        new("install", "ACT 에 설치"),
    ];

    public const string FixTitle = "확인하고 고치기";

    public static async Task FixAndInstall(RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        // 설치 단계에서 막히지 않게 미리 (15초쯤 걸린 뒤에 실패하면 허탈하므로)
        Install.EnsureClosed();
        vm.MapProgress = stage => stage is "opcode" or "CSV" ? "data" : stage == "완료" ? null : "check";

        vm.Start("check");
        var r = await Runner.Run(vm.Reporter, ct, () => { using var _ = ws.Lock(); return Prepare.Run(ws, new PrepareOptions { IdaFallback = false }); }); // IDA 는 고급 모드에서만
        vm.Finish("check");
        vm.Finish("data", r.Steps.Any(s => s.Status == StepStatus.Warning) ? StepState.Warning : StepState.Done);

        // 후크(패킷을 받는 부분) 점검: 점검 자체가 실패했거나, 깨졌는데 고치지 못함 — 이대로는 설치하지 않는다
        if (!r.HooksOk)
        {
            // 고칠 자료(upstream 시그니처, 옛 exe)를 받지 못한 탓이면 그 원인을 먼저 알린다 — 이 단계의 실패만
            // (CSV 등 다른 단계의 네트워크 실패를 "자동으로 고치지 못함"의 원인으로 돌리지 않게)
            if (Friendly.NetworkCause(r.SignatureDownloadFailure) is { } networkCause)
            {
                vm.Finish("check", StepState.Failed, "받지 못함");
                vm.SetResult(ResultKind.Warning, "업데이트 정보를 다 받지 못했습니다", WithNote(r, networkCause, warnings: false));
                return;
            }
            if (r.SignatureCheckFailed)
            {
                vm.Finish("check", StepState.Failed, "확인하지 못함");
                vm.SetResult(ResultKind.Warning, "게임 파일을 확인하지 못했습니다", WithNote(r,
                    "게임 파일을 읽지 못해 플러그인의 핵심 부분(패킷을 받는 부분)이 지금 게임과 맞는지 확인하지 못했습니다. " +
                    "런처가 아직 게임을 업데이트하는 중일 수 있습니다. 게임과 런처를 모두 끈 뒤 다시 누르세요. " +
                    "(자세한 원인은 [자세한 기록]에 있습니다)", warnings: false));
                return;
            }
            vm.Finish("check", StepState.Failed, "자동으로 고치지 못함");
            throw new ToolException("이번 게임 업데이트로 플러그인의 핵심 부분(패킷을 받는 부분)이 바뀌었는데, 자동으로 고치지 못했습니다. " +
                                    "공식 플러그인 업데이트를 기다리거나 잠시 후 다시 시도하세요.");
        }
        if (r.MissingOpcodes.Count > 0)
        {
            // opcode 출처를 받다 실패한 까닭으로 (다른 단계의 네트워크 실패를 "받지 못함"으로 돌리지 않게)
            if (Friendly.NetworkCause(r.OpcodeDownloadFailure) is { } opcodeCause)
            {
                vm.Finish("data", StepState.Failed, "받지 못함");
                vm.SetResult(ResultKind.Warning, "업데이트 정보를 다 받지 못했습니다", WithNote(r, opcodeCause, warnings: false));
                return;
            }
            vm.Finish("data", StepState.Failed, "아직 공개되지 않음");
            vm.SetResult(ResultKind.Warning, "아직 이번 업데이트 정보가 공개되지 않았습니다", WithNote(r,
                "패치에 필요한 정보(opcode)는 보통 게임 업데이트 후 몇 시간 안에 공개됩니다. 잠시 후 다시 누르세요.", warnings: false));
            return;
        }
        // 판단할 수 없으면 설치하지 않는다: 후크 상태를 모르거나(점검 실패), opcode 출처에 빠진 항목이 있어
        // 바뀌었는지 모르는 경우 — 확인 안 된 채로 덮어쓰면 공식본보다 나을 게 없거나 오히려 깨질 수 있다
        if (r.PatchNeeded == null)
        {
            vm.Finish("data", StepState.Warning, "판단할 수 없음");
            vm.Finish("build", StepState.Skipped, "멈춤");
            vm.Finish("install", StepState.Skipped, "멈춤");
            vm.SetResult(ResultKind.Warning, "지금은 고칠지 판단할 수 없습니다", WithNote(r,
                r.Signatures == null
                    ? "게임 파일을 읽지 못해 확인을 끝내지 못했습니다. 게임 업데이트(런처)가 끝났는지 확인하고, 게임을 끈 뒤 다시 누르세요."
                    : "공개된 업데이트 정보가 아직 일부만 있어서 확인을 끝내지 못했습니다. 보통 몇 시간 안에 채워지니 잠시 후 다시 누르세요."));
            return;
        }
        if (r.PatchNeeded == false)
        {
            vm.Finish("build", StepState.Skipped, "필요 없음");
            var status = Status.Collect(ws);
            // 공식 최신본이 이미 게임과 맞는데 ACT 에는 옛 공식본이나 예전 패치본이 있으면 공식 최신본으로 바꾼다
            // (공식 업데이트가 나온 걸 모르고 이 프로그램을 누른 경우 — 해루봇의 플러그인 업데이트와 같은 결과)
            if (status.OfficialUpdate)
            {
                string before = $"{status.Act!.InstalledVersion ?? "?"}{(status.InstalledIsOurs == true ? ", 이 프로그램의 패치본" : "")}";
                SkipIfCancelled(vm, "install", ct);
                vm.Start("install");
                await InstallWithElevation(vm, ws, ct, official: true);
                vm.Finish("install", StepState.Done, $"공식 최신본 {status.BasePluginVersion}");
                vm.SetResult(ResultKind.Success, "공식 최신본으로 업데이트했습니다", WithNote(r,
                    $"공식 플러그인 {status.BasePluginVersion} 버전이 지금 게임 버전과 이미 맞아서, 고친 플러그인 대신 공식 최신본을 ACT 에 설치했습니다 (전에 있던 것: {before}).\n" +
                    "이제 ACT 를 실행하면 됩니다."));
                OfferRestore(vm);
                return;
            }
            vm.Finish("install", StepState.Skipped, "필요 없음");
            vm.SetResult(ResultKind.Success, "지금은 패치가 필요 없습니다",
                WithNote(r, "공식 플러그인이 지금 게임 버전과 이미 맞고, ACT 에도 그 공식 플러그인이 설치되어 있습니다. 따로 고칠 필요가 없습니다."));
            return;
        }

        // 고쳐야 한다: [공식 최신본으로 업데이트]로 시작했어도 다시 확인해 보니 고쳐야 하면 제목을 맞춘다
        vm.Title = FixTitle;
        vm.Start("build");
        await Runner.Run(vm.Reporter, ct, () => { using var _ = ws.Lock(); return Build.Run(ws, new BuildOptions()); });
        vm.Finish("build");

        // 방금 만든 것과 똑같은 패치본이 이미 ACT 에 있으면 다시 설치하지 않는다 ([다시 확인하기]로 확인만 한 경우).
        // 다시 설치하면 그 패치본이 새 백업이 되어 "설치 전으로 되돌리기"가 같은 패치본으로 되돌리게 된다.
        if (Status.Collect(ws).InstalledIsOurs == true)
        {
            vm.Finish("install", StepState.Skipped, "이미 설치됨");
            vm.SetResult(ResultKind.Success, "이미 설치되어 있습니다", WithNote(r,
                "ACT 에는 지금 게임 버전에 맞게 고친 플러그인이 이미 설치되어 있습니다. 따로 할 일 없이 ACT 를 실행하면 됩니다.\n" +
                AfterInstallNote));
            OfferRestore(vm);
            return;
        }

        // 만드는 동안 [취소]를 눌렀으면 설치를 시작하지 않는다
        SkipIfCancelled(vm, "install", ct);
        vm.Start("install");
        await InstallWithElevation(vm, ws, ct);
        vm.Finish("install");
        vm.SetResult(ResultKind.Success, "설치했습니다", WithNote(r,
            "이제 ACT 를 실행하면 됩니다. (해루봇을 쓰면 해루봇에서 'ACT 실행')\n" + AfterInstallNote));
        OfferRestore(vm);
    }

    private const string AfterInstallNote =
        "공식 플러그인 업데이트가 나온 뒤 이 프로그램에서 다시 확인하면 공식 최신본으로 바꿉니다. 해루봇에서 플러그인을 업데이트해도 됩니다.";

    // 결과 설명 끝에 붙이는 참고:
    // · 최신 버전이 아니라 이번 확인에서 뺀 게임 (한쪽 서버를 쉬며 업데이트하지 않은 경우 등)
    // · warnings: 경고나 실패로 끝난 확인 단계가 있음 (결과 자체가 그 실패를 알리는 경우는 false). 공식 플러그인을
    //   받지 못한 이유는 할 일이 있으므로(백신 격리 등) 그대로 보인다.
    private static string WithNote(PrepareResult r, string message, bool warnings = true)
    {
        if (warnings)
        {
            if (r.Steps.FirstOrDefault(s => s.Name == "plugin" && s.Status == StepStatus.Failed) is { } plugin)
                message += $"\n\n공식 플러그인 최신 버전을 받지 못했습니다: {plugin.Message}";
            if (r.Steps.Any(s => s.Status != StepStatus.Ok && !s.Name.StartsWith("game ", StringComparison.Ordinal)))
                message += "\n\n일부 단계에 경고가 있습니다. [자세한 기록]에서 확인하세요.";
        }
        if (r.PatchNeeded == true && r.UnconfirmedOpcodes.Count > 0)
            message += "\n\n아직 공개된 자료가 없어 이 PC 에서 직접 구한 값으로 고쳤습니다. 큰 패치에서는 가끔 틀릴 수 있으니, " +
                       "공개 자료가 나온 뒤(보통 몇 시간 안)에 [다시 확인하기]를 한 번 더 누르세요.";
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

    public static IEnumerable<StepItem> RestoreSteps() => [new("restore", "설치 전 상태로 되돌리기")];

    public static async Task Restore(RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        // 되돌릴 수 없으면 관리자 권한을 청하기 전에 그 이유를 알린다
        var (backup, why) = Install.Restorable(ws);
        if (backup == null)
        {
            vm.Finish("restore", StepState.Warning, "되돌릴 수 없음");
            vm.SetResult(ResultKind.Warning, "되돌리지 않았습니다", why ?? "");
            return;
        }
        vm.Start("restore");
        await Runner.Run(vm.Reporter, ct, () =>
        {
            try
            {
                using var _ = ws.Lock();
                Install.Restore(ws, backup);
                return 0;
            }
            catch (ElevationRequiredException ex)
            {
                Log.Warn($"{ex.Message} - 관리자 권한으로 다시 실행");
                return Check(ElevatedRunner.Run(AppState.CliPath, "restore", ["--root", ws.Root, "--backup", backup]));
            }
        });
        vm.Finish("restore");
        vm.SetResult(ResultKind.Success, "설치 전으로 되돌렸습니다", "설치하기 전의 플러그인 파일로 돌아갔습니다. ACT 를 다시 실행하면 적용됩니다.");
    }

    // ---- 고급 모드: 고른 작업만 ----

    // 실행 순서: prepare -> rsv -> build -> install -> hotfix (배포 묶음은 설치를 막지 않게 맨 뒤)
    public static IEnumerable<StepItem> SelectedSteps(AdvancedViewModel o)
    {
        if (o.AnyPrepare) yield return new("prepare", "입력 데이터 준비 (prepare)");
        if (o.DoRsv) yield return new("rsv", "ACT 로그에서 RSV 가져오기 (rsv)");
        if (o.DoBuild) yield return new("build", "플러그인 만들기 (build)");
        if (o.DoInstall) yield return new("install", o.InstallOfficial ? "ACT 에 공식 최신본 설치 (install --official)" : "ACT 에 설치 (install)");
        if (o.DoHotfix) yield return new("hotfix", "패치본 배포 묶음 (hotfix)");
    }

    public static async Task RunSelected(AdvancedViewModel o, RunViewModel vm, CancellationToken ct)
    {
        var ws = AppState.Workspace;
        if (o.DoInstall) Install.EnsureClosed();
        string? verdict = null;
        if (o.AnyPrepare)
        {
            vm.Start("prepare");
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
            var r = await Runner.Run(vm.Reporter, ct, () => { using var _ = ws.Lock(); return Prepare.Run(ws, options); });
            foreach (var s in r.Steps) vm.Log.Add(new LogLine(s.Status == StepStatus.Ok ? LogKind.Done : LogKind.Warn,
                                                             $"  {(s.Status == StepStatus.Ok ? "OK" : "!!")}  {s.Name}: {s.Message}"));
            verdict = r.Verdict;
            // Complete 는 시그니처 점검 실패(후크 상태를 모름)도 끝나지 않은 것으로 본다 (HooksOk)
            bool ok = r.Complete;
            vm.Finish("prepare", ok ? StepState.Done : StepState.Warning, r.Verdict);
            if (!ok && (o.DoBuild || o.DoInstall))
            {
                vm.SetResult(ResultKind.Warning, "준비가 끝나지 않아 멈췄습니다",
                    r.SignatureCheckFailed
                        ? "시그니처 점검이 실패했습니다. 게임 파일을 읽지 못했을 수 있습니다(런처가 업데이트 중 등). 자세한 기록을 확인하세요."
                        : !r.HooksOk ? "깨진 패킷 후크가 있습니다. input\\signatures 를 확인하세요."
                        : $"opcode 를 못 구한 클라이언트: {string.Join(", ", r.MissingOpcodes)}");
                return;
            }
        }
        if (o.DoRsv)
        {
            vm.Start("rsv");
            var rsv = await Runner.Run(vm.Reporter, ct, () =>
            {
                var files = Rsv.RecentLogs(Rsv.LogFolder(), Rsv.DefaultDays);
                if (files.Count == 0) return null;
                using var _ = ws.Lock();
                return Rsv.Import(ws, files);
            });
            if (rsv == null) vm.Finish("rsv", StepState.Warning, $"최근 {Rsv.DefaultDays}일 ACT 로그가 없습니다 ({Rsv.LogFolder()})");
            else vm.Finish("rsv", rsv.Conflicts.Count > 0 ? StepState.Warning : StepState.Done, rsv.Summary);
        }
        if (o.DoBuild)
        {
            vm.Start("build");
            var b = await Runner.Run(vm.Reporter, ct, () => { using var _ = ws.Lock(); return Build.Run(ws, new BuildOptions { Csv = o.BuildCsv }); });
            vm.Finish("build", b.Warnings.Count > 0 ? StepState.Warning : StepState.Done, string.Join(" / ", b.Warnings));
        }
        if (o.DoInstall)
        {
            SkipIfCancelled(vm, "install", ct);
            vm.Start("install");
            await InstallWithElevation(vm, ws, ct, official: o.InstallOfficial, overlay: o.InstallOverlay);
            vm.Finish("install");
            OfferRestore(vm);
        }
        else OfferOfficialUpdate(vm, ws);
        // 배포 묶음은 설치 뒤에: 만들지 못해도(패치 번호를 모름 등) 설치는 이미 끝났으므로 작업을 실패로 끝내지 않고
        // 결과에 알린다. 여기서 취소해도 마찬가지 (취소 결과는 "ACT 는 그대로"라고 알리므로 설치한 뒤에는 쓰지 않는다)
        HotfixResult? hotfix = null;
        string? hotfixError = null;
        if (o.DoHotfix && ct.IsCancellationRequested)
        {
            vm.Finish("hotfix", StepState.Skipped, "취소함");
            hotfixError = "취소해서 만들지 않았습니다.";
        }
        else if (o.DoHotfix)
        {
            vm.Start("hotfix");
            try
            {
                hotfix = await Runner.Run(vm.Reporter, ct, () =>
                {
                    using var _ = ws.Lock();
                    // 패치 번호를 대응표에서 못 구하면 Core 는 "--tag 로 지정"을 안내하지만 화면에서는 지정할 수 없다
                    if (MaterialState.Load(ws) is { } prepared && HotfixPackage.PatchTag(prepared.GameVersions) == null)
                        throw new ToolException("게임 버전에 맞는 패치 번호를 알 수 없어(대응표가 아직 갱신되지 않음) 배포 묶음을 만들지 못했습니다. " +
                                                "명령줄에서 hotfix --tag 7.58 처럼 패치 번호를 직접 지정해 만드세요.");
                    return HotfixPackage.Create(ws);
                });
            }
            catch (OperationCanceledException)
            {
                vm.Finish("hotfix", StepState.Skipped, "취소함");
                hotfixError = "취소해서 만들지 않았습니다.";
            }
            catch (Exception ex) when (Errors.Recoverable(ex))
            {
                hotfixError = ex is ToolException ? ex.Message : Errors.Describe(ex);
                if (ex is not ToolException) Errors.WriteLog(ex, "hotfix");
                vm.Log.Add(new LogLine(LogKind.Error, $"[hotfix] {hotfixError}"));
                vm.Finish("hotfix", StepState.Failed, hotfixError);
            }
            if (hotfix != null)
            {
                string upload = hotfix.UploadCommand;
                vm.Log.Add(new LogLine(LogKind.Info, "  올리기 (release-notes.md 의 '알려진 문제' 를 확인한 뒤):"));
                vm.Log.Add(new LogLine(LogKind.Info, "  " + upload));
                vm.Finish("hotfix", hotfix.Warnings.Count > 0 ? StepState.Warning : StepState.Done,
                    hotfix.Warnings.Count > 0 ? string.Join(" / ", hotfix.Warnings) : $"{hotfix.Tag} - zip + 릴리스 설명");
                string folder = System.IO.Path.GetDirectoryName(hotfix.ZipPath)!;
                vm.AddAction("올리기 명령 복사", Wpf.Ui.Controls.SymbolRegular.Copy24, async action =>
                {
                    action.Text = RunViewModel.TryCopy(upload) ? "복사했습니다" : "복사하지 못했습니다. 다시 누르세요";
                    await Task.Delay(2000);
                    action.Text = "올리기 명령 복사";
                });
                vm.AddAction("패치본 폴더 열기", Wpf.Ui.Controls.SymbolRegular.FolderOpen24, _ =>
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
                    return Task.CompletedTask;
                });
            }
        }
        // 끝까지 했어도 경고나 실패로 끝난 단계가 있으면 성공으로 보이지 않게
        bool clean = vm.Steps.All(s => s.State is not (StepState.Warning or StepState.Failed)) && hotfixError == null;
        vm.SetResult(clean ? ResultKind.Success : ResultKind.Warning,
            clean ? "선택한 작업을 마쳤습니다" : "선택한 작업을 마쳤지만 확인할 것이 있습니다",
            (verdict ?? "자세한 내용은 기록을 보세요.") +
            (hotfix != null ? $"\n패치본 배포 묶음: {hotfix.ZipPath}\nrelease-notes.md 의 '알려진 문제' 를 확인한 뒤 [올리기 명령 복사]로 올리면 됩니다." : "") +
            (hotfixError != null ? $"\n패치본 배포 묶음: {hotfixError}" : ""));
    }

    // ---- 공통 ----

    // official: 패치본 대신 input\plugin 의 공식 최신본을 설치
    // overlay=false: OverlayPlugin opcode 는 넣지 않는다 (공식본 설치는 원래 넣지 않음)
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
                Log.Warn($"{ex.Message} - 관리자 권한으로 다시 실행 (Windows 확인 창에서 '예')");
                return Check(ElevatedRunner.Run(AppState.CliPath, "install", plan.ToCliArgs(ws)));
            }
        });

    // 고급 모드: 공식 최신본이 이미 게임과 맞을 때 고르면 설치 (낮은 버전을 일부러 두었을 수 있으므로 자동으로 하지 않는다)
    private static void OfferOfficialUpdate(RunViewModel vm, Workspace ws)
    {
        var status = Status.Collect(ws);
        if (!status.OfficialUpdate) return;
        vm.AddAction($"공식 최신본({status.BasePluginVersion})으로 업데이트", Wpf.Ui.Controls.SymbolRegular.ArrowSync24, _ =>
        {
            vm.ActionNote = null; // 다시 누르면 지난 안내는 지운다
            // 진행 화면으로 넘어가기 전에 확인한다 (넘어간 뒤에 실패하면 이 버튼이 있던 결과 화면으로 돌아올 수 없다)
            try { Install.EnsureClosed(); }
            catch (AppsRunningException ex)
            {
                vm.ActionNote = Friendly.Describe(ex).Message;
                return Task.CompletedTask;
            }
            // 결과를 보는 사이에 바뀌었을 수 있다 (해루봇이 플러그인을 업데이트함 등)
            var now = Status.Collect(ws);
            if (!now.OfficialUpdate)
            {
                vm.ActionNote = "지금은 공식 최신본으로 바꿀 것이 없습니다. [확인]을 누른 뒤 상태를 다시 확인하세요.";
                return Task.CompletedTask;
            }
            string version = now.BasePluginVersion ?? "?";
            Navigator.ShowRun("공식 최신본으로 업데이트", [new("install", $"공식 최신본 {version} 버전을 ACT 에 설치")],
                async (run, token) =>
                {
                    Install.EnsureClosed();
                    run.Start("install");
                    await InstallWithElevation(run, ws, token, official: true);
                    run.Finish("install");
                    run.SetResult(ResultKind.Success, "공식 최신본으로 업데이트했습니다",
                        $"ACT 에 공식 플러그인 {version} 버전을 설치했습니다. 이제 ACT 를 실행하면 됩니다.");
                    OfferRestore(run);
                });
            return Task.CompletedTask;
        });
    }

    // 관리자 쪽 CLI 의 종료 코드 -> 예외 (4: ACT/게임 실행 중 — 종료 코드로는 어느 것인지 모르므로 여기서 다시 확인해
    // 알리고, 그새 꺼졌으면 목록 없이 알린다). 그 밖의 실패는 관리자 쪽이 남긴 마지막 오류 문장을 그대로 보인다.
    private static int Check(int code)
    {
        switch (code)
        {
            case 0:
                return 0;
            case 4:
                Install.EnsureClosed();
                throw new AppsRunningException([]);
            default:
                throw new ToolException(ElevatedRunner.LastError is { Length: > 0 } error
                    ? error
                    : "관리자 권한으로 진행하다가 실패했습니다. 자세한 기록을 확인하세요.");
        }
    }

    private static void OfferRestore(RunViewModel vm)
    {
        vm.AddAction("설치 전으로 되돌리기", Wpf.Ui.Controls.SymbolRegular.ArrowUndo24, _ =>
        {
            Navigator.ShowRun("설치 전으로 되돌리기", RestoreSteps(), Restore);
            return Task.CompletedTask;
        });
    }
}
