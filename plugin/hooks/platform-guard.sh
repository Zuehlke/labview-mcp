# SessionStart hook for the LabVIEW MCP plugin.
#
# Purpose 1 (the reason this hook exists): the plugin ships a win-x64 binary that talks to
# LabVIEW.exe over a Windows-only local interface. Nothing in the plugin/marketplace schema
# declares a platform, so a macOS or Linux user can install the plugin cleanly and then get
# an MCP server that simply fails to start, with no explanation. This hook is the only place
# that can say why. It is written in POSIX sh — NOT PowerShell — precisely because it has to
# run on the non-Windows host it is meant to warn, where PowerShell is usually absent.
#
# Purpose 2 (best effort): the bundled documentation agent needs python-docx and a Chromium
# browser (Edge/Chrome) for SVG->PNG diagram rendering. On Windows we check for both and WARN
# if they are missing. We never block.
#
# Output contract: a SessionStart hook shows a message by printing {"systemMessage":"..."} to
# stdout and exiting 0. We emit that only when we have something to say; otherwise we stay
# silent so a healthy Windows session sees nothing.

emit() {
    # Join the (possibly multi-line) message with literal \n and wrap it as a systemMessage.
    # Messages here are plain ASCII with no double quotes or backslashes, so no other escaping
    # is required.
    joined=$(printf '%s' "$1" | awk 'BEGIN{ORS=""} {printf "%s%s", (NR>1?"\\n":""), $0}')
    printf '{"systemMessage":"%s"}\n' "$joined"
}

os=$(uname -s 2>/dev/null || echo unknown)

case "$os" in
    Darwin | Linux | *BSD* | SunOS)
        emit "LabVIEW MCP requires Windows x64 with LabVIEW 2026 running. Its MCP server is a win-x64 binary that discovers LabVIEW.exe over a Windows-only local interface, so on ${os} the server will not start and no lvai_* tools will be available. The bundled agents and reference documents still load. Install this plugin on a Windows machine with LabVIEW 2026 to use it."
        exit 0
        ;;
esac

# --- Windows (Git Bash / MSYS / Cygwin, or an unknown shell): warn-only dependency checks ---
warn=""

# python-docx imports as `docx`. Find an interpreter, then test the import.
py=""
for c in python python3 py; do
    if command -v "$c" >/dev/null 2>&1; then
        py="$c"
        break
    fi
done
if [ -z "$py" ]; then
    warn="${warn}
- Python was not found on PATH. The documentation agent needs Python with python-docx installed."
elif ! "$py" -c "import docx" >/dev/null 2>&1; then
    warn="${warn}
- python-docx is not installed (fix: ${py} -m pip install python-docx). The documentation agent needs it to write .docx files."
fi

# A Chromium browser (Edge or Chrome) for headless SVG->PNG diagram rendering.
browser=""
for b in msedge chrome chromium chromium-browser google-chrome; do
    if command -v "$b" >/dev/null 2>&1; then
        browser="$b"
        break
    fi
done
if [ -z "$browser" ] && [ -n "$PROGRAMFILES" ]; then
    for p in \
        "$PROGRAMFILES/Microsoft/Edge/Application/msedge.exe" \
        "$PROGRAMFILES/Google/Chrome/Application/chrome.exe"; do
        if [ -f "$p" ]; then
            browser="$p"
            break
        fi
    done
fi
if [ -z "$browser" ]; then
    warn="${warn}
- No Chromium browser (Edge or Chrome) was found. The documentation agent renders its structure and UML diagrams headless through one."
fi

if [ -n "$warn" ]; then
    emit "LabVIEW MCP: the documentation agent has optional dependencies that appear to be missing (warning only — the server and the other agents work regardless):${warn}"
fi

exit 0
