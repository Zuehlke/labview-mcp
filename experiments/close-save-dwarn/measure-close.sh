#!/bin/sh
# Mark the LabVIEW log, or measure what has been appended since the mark.
#   mark.sh mark            - remember the current size
#   mark.sh read <label>    - print the warnings added since the mark, and re-mark
F=/c/Users/jcm/AppData/Local/Temp/LabVIEW_32_26.3.1f1_interactive_jcm_cur.txt
S=/c/Users/jcm/AppData/Local/Temp/claude/C--Projects-LabVIEWMCP/faf3b45e-abd1-4876-b4e2-bf159802aa5f/scratchpad
case "$1" in
  mark) stat -c%s "$F" > "$S/abmark.txt"; echo "marked at $(cat $S/abmark.txt)" ;;
  read)
    M=$(cat "$S/abmark.txt")
    tail -c +$((M + 1)) "$F" > "$S/ab.txt"
    # grep -c on a file prints one number; the `|| true` keeps a zero match from
    # failing the script, and tr strips the CR the Windows shell leaves behind.
    D=$(grep 'DWarn' "$S/ab.txt" | grep 'source' | wc -l | tr -d ' \r')
    E=$(grep 'DestroyPlatformEvent' "$S/ab.txt" | grep 'source' | wc -l | tr -d ' \r')
    P=$(grep 'bad parent in MoveItem' "$S/ab.txt" | grep 'source' | wc -l | tr -d ' \r')
    printf '%-24s warnings=%-4s DestroyPlatformEvent=%-4s MoveItem=%-4s bytes=%s\n' \
           "$2" "$D" "$E" "$P" "$(stat -c%s "$S/ab.txt")"
    stat -c%s "$F" > "$S/abmark.txt"
    ;;
esac
