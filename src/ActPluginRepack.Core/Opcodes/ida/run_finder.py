# IDA 헤드리스 래퍼 (ActPluginRepack prepare 가 실행).
#   idat -A -S"run_finder.py <region>" ffxiv_dx11.exe
#
# 같은 폴더에 Yarukon/FFXIVNetworkOpcodes 의 ffxiv_opcode_finder.py / signatures.json /
# machina.template 이 받아져 있어야 한다. 하는 일:
#   1) 자동 분석이 끝나길 기다린다.
#   2) signatures.override.json 을 signatures.json 에 덮어쓴다 (CN 이 아닌 클라이언트용 보정)
#      — finder 가 시스템 기본 인코딩으로 읽으므로 signatures.json 은 항상 ASCII 로 다시 쓴다.
#   3) finder 를 지역=CN 으로 강제해 실행한다. 7.5 이후 클라이언트가 통합돼 Global/KR exe 도 opcode 가
#      CN 과 같지만, Yarukon 이 관리하는 시그니처는 CN 것뿐이다 (pdb 경로로는 Global/KR 둘 다 'Global').
#   4) 결과는 finder 가 output/CN_<날짜>/ 에 쓴다 (도구는 opcodes.json 을 읽음). DB 는 남기지 않는다.
import json, os, traceback
import ida_auto, ida_bytes, ida_loader, ida_search, idc

here = os.path.dirname(os.path.realpath(__file__))
region = idc.ARGV[1] if len(idc.ARGV) > 1 else "global"

# IDA 7.x 호환: finder 는 IDA 9 의 ida_bytes.find_bytes 를 쓴다.
if not hasattr(ida_bytes, "find_bytes"):
    def _find_bytes(pattern, range_start, range_end=idc.BADADDR, **_):
        return ida_search.find_binary(range_start, range_end, pattern, 16, ida_search.SEARCH_DOWN)
    ida_bytes.find_bytes = _find_bytes


def merge(dst, src):
    for k, v in src.items():
        if k.startswith("//"):
            continue
        if isinstance(v, dict) and isinstance(dst.get(k), dict) and "Signature" not in v:
            merge(dst[k], v)
        else:
            dst[k] = v


code = 1
try:
    ida_auto.auto_wait()
    sig_path = os.path.join(here, "signatures.json")
    with open(sig_path, encoding="utf-8") as f:
        sigs = json.load(f)
    if region != "simplified-chinese":
        with open(os.path.join(here, "signatures.override.json"), encoding="utf-8") as f:
            merge(sigs, json.load(f))
    # finder 는 시스템 기본 인코딩(한국어 Windows 면 cp949)으로 읽으므로 항상 ASCII 로 다시 쓴다
    with open(sig_path, "w", encoding="ascii") as f:
        json.dump(sigs, f, ensure_ascii=True, indent=2)

    path = os.path.join(here, "ffxiv_opcode_finder.py")
    src = open(path, encoding="utf-8").read()
    marker = 'print(f"{Region} {version_s}'
    if marker not in src:
        raise RuntimeError("region marker not found in ffxiv_opcode_finder.py")
    src = src.replace(marker, "Region = 'CN'\n" + marker, 1)
    try:
        exec(compile(src, path, "exec"), {"__file__": path, "__name__": "__main__"})
    except Exception:
        # machina.txt 는 결과 출력 앞부분에서 이미 쓰인다 - 뒤쪽 부가 산출물 실패는 무시
        traceback.print_exc()
    code = 0
except Exception:
    traceback.print_exc()

if os.environ.get("ACTREPACK_KEEP_IDB") != "1":
    ida_loader.set_database_flag(ida_loader.DBFL_KILL)
idc.qexit(code)
