"""
One-screen check of the demo chain: FaceID simulator + PPE box -> station -> cloud. Read-only.
  python station/diag.py [--data ~/.local/share/dsa-tram] [--sim http://localhost:8095] [--ppe http://localhost:8096]
"""
import argparse
import json
import os
import sqlite3
import urllib.request


def fetch(url):
    try:
        with urllib.request.urlopen(url, timeout=5) as r:
            return json.load(r)
    except Exception as ex:          # noqa: BLE001 - a diagnostic prints every failure
        return f"không kết nối được ({type(ex).__name__})"


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--data", default=os.path.expanduser(os.environ.get("TRAM_DATA", "~/.local/share/dsa-tram")))
    p.add_argument("--sim", default=f"http://localhost:{os.environ.get('FACESIM_PORT', '8095')}")
    p.add_argument("--ppe", default=f"http://localhost:{os.environ.get('PPEBOX_PORT', '8096')}")
    a = p.parse_args()

    print("== Simulator", a.sim)
    status = fetch(a.sim + "/api/status")
    if isinstance(status, dict):
        hb = status["heartbeat"]
        print(f"   thiết bị {status['deviceId']} · {status['people']} khuôn mặt · hàng đợi {status['backlog']} · heartbeat {'OK' if hb['ok'] else 'LỖI'} ({hb['detail']})")
        for e in fetch(a.sim + "/api/events")[:5]:
            print(f"   {e['created'][11:19]}  PIN {e['pin']:<9} điểm {e['score']:<6} {e['status']:<9} {e['detail']}")
    else:
        print("  ", status)

    print("== AI Box PPE", a.ppe)
    box = fetch(a.ppe + "/api/status")
    if isinstance(box, dict):
        last = box.get("last") or {}
        seen = box.get("secondsSinceFrame")
        print(f"   thiet bi {box['deviceId']} · che do {box['mode']} · theo doi {', '.join(box['items'])} · "
              f"khoa receiver {'OK' if box['receiverKeySet'] else 'CHUA DAT'}")
        print(f"   vung ROI: {box['roiSource']}{' · ' + box['roiError'] if box['roiError'] else ''}")
        for roi in box.get("rois", []):
            shape = f"{len(roi['polygon'])} dinh" if len(roi["polygon"]) >= 3 else "toan khung hinh"
            print(f"     {roi['roiId'] or '(khong ma)':<10} {shape}")
        print(f"   da gui {box['sent']} · loi {box['failed']} · ban tin cuoi {last.get('at', '—')} "
              f"({last.get('people', '—')} nguoi, {last.get('status', '—')})")
        print(f"   khung hinh: {box['frames']} da nhan" + (f", moi nhat {seen:.0f}s truoc" if isinstance(seen, (int, float)) else ", chua co (mo trang va cho phep camera)"))
    else:
        print("  ", box)

    path = os.path.join(a.data, "parking.db")
    print("== Trạm", path)
    if not os.path.exists(path):
        print("   chưa có database: trạm chưa chạy lần nào (make station)"); return
    db = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    one = lambda sql: db.execute(sql).fetchone()
    cursor = one("select LastId, LastKey from SyncCursors where Peer = 'platform:master'")
    print(f"   danh sách từ Cloud: phiên bản {cursor[0] if cursor else '—'} ({cursor[1] if cursor else 'chưa kéo'}) · "
          f"{one('select count(*) from People')[0]} người · {one('select count(*) from PersonIdentities')[0]} FaceId")
    for source, ext, pid in db.execute("select Source, ExternalId, PersonId from PersonIdentities order by Source, ExternalId limit 10"):
        print(f"     {source}/{ext} -> người #{pid}")
    for cam, last in db.execute("select Id, LastMessageUtc from Cameras where ExternalDevice is not null"):
        print(f"   camera {cam}: tin cuối {last or 'chưa có'} (UTC)")
    print("   sự kiện gần nhất:")
    for lane, pid, conf, outcome, reason, at in db.execute(
            "select LaneId, PersonId, FaceConfidence, Outcome, Reason, CreatedUtc from Events order by CreatedUtc desc limit 5"):
        print(f"     {at[11:19]} {lane:<7} người {pid or '—':<5} {conf:<5} {outcome:<12} {reason}")
    print("   gửi lên Cloud (outbox):")
    for kind, status_, n, err in db.execute("select EntityType, Status, count(*), max(LastError) from Outbox group by EntityType, Status"):
        print(f"     {kind:<14} {status_:<8} {n:>4}  {err or ''}")


if __name__ == "__main__":
    main()
