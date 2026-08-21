#Requires AutoHotkey v2.0
#SingleInstance Force

a4Frequency := 431

; Pause/Break remains active even while other hotkeys are suspended.
#SuspendExempt
Pause::
{
    Suspend(-1)

    ; play an A4
    if(!A_IsSuspended)
    {
        SoundBeep(a4Frequency, 100)
    }
}
#SuspendExempt False

$RButton::
{
    if KeyWait("RButton", "T0.2")
    {
        Click("Right")
        return
    }

    Send("{MButton down}")
    KeyWait("RButton")
    Send("{MButton up}")
}

; Piano mode requires Scroll Lock.
; Equal-temperament tuning based on C4 = 256 Hz.

nextDuration := 250
sendMusicText := false

#HotIf GetKeyState("ScrollLock", "T")

Tab::
{
    global sendMusicText
    sendMusicText := !sendMusicText
    SoundBeep(a4Frequency, sendMusicText ? 300 : 100)
}

Esc::
{
    global sendMusicText
    sendMusicText := false
}

,::
{
    global sendMusicText

    if sendMusicText
    {
        SendText("-")
    }

    SoundBeep(a4Frequency, 300)
}

.::
{
    global sendMusicText

    if sendMusicText
    {
        SendText("·")
    }

    SoundBeep(a4Frequency, 100)
}

/::
{
    global sendMusicText

    if sendMusicText
    {
        SendText(" ")
    }
}

; Set the duration of the next note.
1::SetNextLength(63)
2::SetNextLength(125)
3::SetNextLength(167)
4::SetNextLength(250)
5::SetNextLength(311)
6::SetNextLength(370)
7::SetNextLength(440)
8::SetNextLength(500)
9::SetNextLength(587)
0::SetNextLength(1000)

c::PlayNote("C3", 128)
v::PlayNote("D3", 144)
b::PlayNote("E3", 161)
n::PlayNote("F3", 171)
m::PlayNote("G3", 192)

a::PlayNote("A3", 215)
s::PlayNote("B3", 242)
d::PlayNote("C4", 256)
f::PlayNote("D4", 287)
g::PlayNote("E4", 323)
h::PlayNote("F4", 342)
j::PlayNote("G4", 384)
k::PlayNote("A4", 431)
l::PlayNote("B4", 483)
`;::PlayNote("C5", 512)

q::PlayNote("A4", 431)
w::PlayNote("B4", 483)
e::PlayNote("C5", 512)
r::PlayNote("D5", 575)
t::PlayNote("E5", 645)
y::PlayNote("F5", 683)
u::PlayNote("G5", 767)
i::PlayNote("A5", 861)
o::PlayNote("B5", 967)
p::PlayNote("C6", 1024)

#HotIf

SetNextLength(duration)
{
    global nextDuration
    nextDuration := duration
}

PlayNote(note, frequency)
{
    global nextDuration, sendMusicText

    duration := nextDuration

    if sendMusicText
    {
        ; Approximately one space per 167 ms.
        spaceCount := Round(duration / 167)
        spacing := ""

        Loop spaceCount
            spacing .= " "

        SendText(note . spacing)
    }

    SoundBeep(frequency, duration)
}
