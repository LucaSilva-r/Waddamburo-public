# Song-select tutorial observations

Behavioral observations from a user-driven original-game session on 2026-11-12.
Raw logs and the generated scene report remain outside the public repository.
These are requirements for an independent implementation, not copied engine code.

## Waiwai

The tutorial is a dedicated special folder before the switch to normal song select.
The report identifies its displayed name as ワイワイ演奏説明. Selecting it requests a
tutorial rather than a playable song. Its catalog assignment includes one special
placeholder, so “no songs” must not be interpreted as requiring a zero item count
in the movie protocol. The folder disappears from the returned selection in this
session after the tutorial has been watched.

The scene loads the dedicated `waiwai_tutorial` movie and starts it once. It is an
authored animated sequence, not a single prerecorded video stream. It requests
`PlayVO`, `PlaySE`, and `AllFinish`; completion returns to Waiwai song select.
The observed audio families are `VO_WAIATTR`, `SE_WAITUTOR`, and the tutorial
background music `JINGLE_WAITUTOR`. The observed sequence lasted about 38 seconds.
Host entry points must remain distinct from similarly named movie callbacks.

## AI Battle

The tutorial is the first special folder in AI Battle song select. Its catalog
assignment also includes a special placeholder. Selecting it loads a dedicated
`ghost/tutorial` movie, `ghost/common_bg`, and a timer overlay. The host supplies
which player sides are joined. The tutorial uses a 120-second timer, paused for
initial preparation and resumed when requested.

The user observed a slideshow presentation. The movie requests sound selection,
background music changes, stream readiness/playback, pause handling, and timer
start. Readiness must reflect the actual requested stream; returning a constant
ready flag is not established behavior. This session visited two stream playback
requests. The observed sound families include `VO_AIBTL_INFO`, `SE_AIBTL_INFO`, and `SE_COM`.
Finishing returns to AI Battle song select. The watched session lasted about
56 seconds; that duration includes user-controlled interaction and is not a fixed
tutorial length.

## Scope still to establish

This single session does not establish every skip/back action, timer-expiry
behavior, page count, stream identity, or whether watched-state persists across
credits/restarts. Those should be checked against user-supplied movie data and,
where necessary, another targeted session before being treated as requirements.

The tutorial sound constants resolve through the supplied nuSound bank-name table;
they are distinct from the AI song-select voices. The first generated trace report
misidentified a reused bank object, so its bank-name annotation was checked against
the numeric movie request and the asset table before finalizing this mapping.
