# SPDX-License-Identifier: MIT

"""Rendezvous derivation — two phones agreeing where to meet from their tags alone.

Port of the C# reference ``AetherNet.Rendezvous`` (src/AetherNet.Core/Rendezvous/). Verified
byte-for-byte against ``fixtures/meeting/meeting_basic.json``.
"""

from aethernet.meeting.meeting import Meeting, hosts_the_group

__all__ = ["Meeting", "hosts_the_group"]
