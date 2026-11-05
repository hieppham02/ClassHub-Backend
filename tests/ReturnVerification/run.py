"""Run integration checks in a disposable MariaDB database; never copy live rows."""
import importlib.util
import os
from pathlib import Path
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[4]
spec = importlib.util.spec_from_file_location("classhub_local_db", ROOT / "Firmware/tools/ai_data/db.py")
db = importlib.util.module_from_spec(spec)
spec.loader.exec_module(db)

name = "classhub_return_test_" + uuid.uuid4().hex[:12]
assert len(name) == 33 and name.startswith("classhub_return_test_") and name.replace("_", "").isalnum()
tables = ("borrowing_session", "borrowing_equipment", "cabinet", "cabinet_qr_access", "audit_log", "equipment", "account", "room", "building", "class_slot", "iot_node", "iot_sensor")
config = db.settings()
def val(*keys, default=""):
    return next((config[k] for k in keys if k in config), default)
def quote(value):
    return '"' + str(value).replace('"', '""') + '"'
connection = ";".join(key + "=" + quote(value) for key, value in {
    "Server": val("server", "host", "data source", default="localhost"),
    "Port": val("port", default="3306"),
    "User ID": val("user id", "userid", "uid", "user", "username"),
    "Password": val("password", "pwd"), "Database": name,
}.items())
created = False
try:
    db.run(f"CREATE DATABASE `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;")
    created = True
    for table in tables:
        db.run(f"CREATE TABLE `{name}`.`{table}` LIKE `{table}`;")
    ddl = (ROOT / "Firmware/docs/return-verification.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + ddl)
    rfid_ddl = (ROOT / "Firmware/docs/rfid-session.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + rfid_ddl)
    receipt_ddl = (ROOT / "Firmware/docs/receipt-ack.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + receipt_ddl)
    health_ddl = (ROOT / "Firmware/docs/cabinet-health.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + health_ddl.replace("room_id VARCHAR(50)","room_id VARCHAR(20)",1))
    operations_ddl = (ROOT / "Firmware/docs/operations-telemetry.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + operations_ddl.replace("room_id VARCHAR(50)","room_id VARCHAR(20)",1))
    width_ddl = (ROOT / "Firmware/docs/cabinet-room-id-width.sql").read_text(encoding="utf-8")
    db.run(f"USE `{name}`;\n" + width_ddl)
    env = dict(os.environ, CLASSHUB_RETURN_TEST_CONNECTION=connection)
    project = Path(__file__).with_name("ReturnVerification.csproj")
    subprocess.run(["dotnet", "run", "--project", str(project), "--verbosity", "quiet", "--property:OutputPath=" + str(ROOT / "Firmware/.codex-temp/return-test-build"), "--property:UseAppHost=false"], env=env, check=True)
    # Exercise the shipped backup/restore verifier on fixtures, never on live data.
    backup_spec = importlib.util.spec_from_file_location("classhub_backup", ROOT / "Firmware/tools/server/database_backup.py")
    backup_module = importlib.util.module_from_spec(backup_spec)
    backup_spec.loader.exec_module(backup_module)
    test_settings = dict(config, database=name)
    backup_db = backup_module.Database(test_settings, db.MYSQL, db.MYSQL.with_name("mysqldump.exe"))
    db.run(f"CREATE TABLE `{name}`.backup_probe (id INT PRIMARY KEY, payload LONGBLOB NOT NULL) ENGINE=InnoDB; INSERT INTO `{name}`.backup_probe VALUES (1,0xffd80000ffd9);")
    import tempfile
    with tempfile.TemporaryDirectory(prefix="backup-fixture-", dir=ROOT / "Firmware/.codex-temp") as folder:
        backup_file = Path(folder) / "fixture.sql"
        backup_db.backup(backup_file)
        original_sql = backup_db.sql
        binary_restored = []
        def checked_sql(sql, database=None, user=None, password=None):
            if sql.startswith("CHECK TABLE `backup_probe`"):
                binary_restored.append(original_sql("SELECT HEX(payload) FROM backup_probe WHERE id=1;", database, user, password).strip())
            return original_sql(sql, database, user, password)
        backup_db.sql = checked_sql
        result = backup_db.verify(backup_file)
        assert result["tables"]["backup_probe"] == 1 and binary_restored == ["FFD80000FFD9"]
        try:
            backup_db.backup(backup_file)
            raise AssertionError("Backup overwrite was permitted")
        except FileExistsError:
            pass
        with backup_file.open("ab") as target:
            target.write(b"-- changed")
        try:
            backup_db.verify(backup_file)
            raise AssertionError("Corrupted backup was permitted")
        except RuntimeError as error:
            assert "integrity" in str(error)
    print("PASS: isolated backup/restore round trip, binary fixture, no overwrite, checksum rejection")
finally:
    # Only this generated disposable schema is ever dropped, including on a failed check.
    assert name.startswith("classhub_return_test_") and len(name) == 33
    if created:
        db.run(f"DROP DATABASE `{name}`;")
