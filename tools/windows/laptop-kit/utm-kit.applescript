on run argv
    set scratchID to "50586ED7-B2DA-47BC-BED5-F7D3919DE097"
    set timeLimit to (item 1 of argv) as integer
    set payload to item 2 of argv
    tell application id "com.utmapp.UTM"
        set vm to virtual machine id scratchID
        if (name of vm is not "cod2-scratch") or (id of vm is not scratchID) then error "Scratch VM identity mismatch"
        with timeout of (timeLimit + 10) seconds
            set p to execute of vm at "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" with arguments {"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", payload} with output capturing
            repeat timeLimit times
                set receipt to get result of p
                if exited of receipt then
                    if (exit code of receipt) is not 0 then error (output text of receipt) & (error text of receipt)
                    return output text of receipt
                end if
                delay 1
            end repeat
        end timeout
        error "The Windows guest command exceeded its time limit."
    end tell
end run
