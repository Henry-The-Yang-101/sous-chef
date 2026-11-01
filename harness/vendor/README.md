# QR encoder

`qrcodegen.py` is the unmodified Python implementation from Project Nayuki's
[QR Code generator v1.8.0](https://github.com/nayuki/QR-Code-generator/tree/v1.8.0),
distributed under the MIT license reproduced at the top of that file. It has no
runtime dependencies. `harness/sharing.py` renders its matrix to SVG locally;
phone links and access tokens are never sent to a QR image service.

Source SHA-256: `b089855caf16185c61421ea4927c1b213cf9468940d71fa8ab11ef83662dcc84`.
