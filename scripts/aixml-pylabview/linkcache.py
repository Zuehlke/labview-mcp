# -*- coding: utf-8 -*-
"""The reader's caches of LINKED FILES - a typedef's .ctl, a subVI's connector pane - kept across
reads.

vi2aixml.py runs as __main__ in the server's resident worker, freshly for every read, so a cache
in its own globals was empty again on the next read: reading one DQMH library ten times read each
.ctl ten times. An imported module stays loaded for the worker's life (worker.py replaces a worker
after 300 calls), so the caches live here. Every key holds the file's path, size and write time
(vi2aixml._file_key) and the link-check mode, so a changed file is a new key and read again.
"""

# (normcased path, mtime_ns, size, full check) -> vi2aixml._ctl_typedef's answer
TYPEDEFS = {}

# (normcased path, mtime_ns, size, full check) -> vi2aixml.callee_pane's answer
CALLEE_PANES = {}
