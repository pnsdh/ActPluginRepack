// 새 작업(prepare)이 시작되면(Http.ConnectionEpoch 가 늘면) 잊어야 하는 기억을 가진 곳이 쓴다 (GameVersions).
// (받는 도중에 작업이 바뀔 수 있는 기억은 받기 전의 번호를 같이 적는다 — Thaliak.Patches)
// 인터넷을 다시 연결한 뒤의 실행이 조금 전 실패나 받아 둔 목록 때문에 틀리게 안내하지 않게. 잠금은 쓰는 쪽이 한다.
internal sealed class EpochWatch
{
    private int _seen;

    // 지난번에 물은 뒤로 새 작업이 시작됐으면 true (같은 작업에서 다시 물으면 false)
    public bool Changed()
    {
        int now = Http.ConnectionEpoch;
        if (now == _seen) return false;
        _seen = now;
        return true;
    }
}
