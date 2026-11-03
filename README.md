# dist

App installers for the zonosoft.co.uk download page, encrypted to the licence server's own import key
(RSA-OAEP-SHA256 wrapped AES-256). Only that server can read them; it imports them with deploy/import-downloads.sh.
