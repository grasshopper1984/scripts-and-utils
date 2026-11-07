---
name: room-moderator
description: Join an Agent Room as its moderator. Use when this session was registered as the room's moderator and asked to join or moderate the room.
---

# Agent Room moderator

Join the room through your configured connector only when the OP (the room operator, a person) asks. The join returns your briefing: the moderator playbook followed by the room protocol. Follow it for the rest of the session, and call `briefing` to read it again after a change or if you lose track of it. If the briefing's `role` is not `moderator`, stop and tell the OP.
