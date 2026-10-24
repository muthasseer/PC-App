"""Writes res/values/strings.xml, res/values-si/strings.xml and res/values-ta/strings.xml from strings.py."""
import pathlib
import re
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from strings import STRINGS  # noqa: E402

RES = HERE.parent / "src" / "main" / "res"
LANGS = [("values", 0), ("values-si", 1), ("values-ta", 2)]


def escape(text: str) -> str:
    text = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
    text = text.replace("\\", "\\\\").replace("'", "\\'").replace('"', '\\"')
    if text[:1] in ("@", "?"):
        text = "\\" + text
    return text


def main() -> None:
    for key, values in STRINGS.items():
        if not re.fullmatch(r"[a-z][a-z0-9_]*", key):
            raise SystemExit(f"bad key {key}")
        args = [sorted(re.findall(r"%\d\$[sd]", v)) for v in values]
        if any(a != args[0] for a in args):
            raise SystemExit(f"{key}: placeholders differ between languages: {args}")
    for folder, index in LANGS:
        lines = ['<?xml version="1.0" encoding="utf-8"?>',
                 "<!-- Generated from app/strings/strings.py by app/strings/generate.py. Do not edit. -->"]
        if index:
            lines.append("<!-- First translation: to be reviewed by a native speaker before release. -->")
        lines.append("<resources>")
        for key, values in STRINGS.items():
            lines.append(f'    <string name="{key}">{escape(values[index])}</string>')
        lines.append("</resources>")
        out = RES / folder / "strings.xml"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text("\n".join(lines) + "\n", encoding="utf-8")
        print(f"wrote {out.relative_to(HERE.parent.parent)} ({len(STRINGS)} strings)")


if __name__ == "__main__":
    main()
