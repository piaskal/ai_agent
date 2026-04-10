import string

alphabet = string.ascii_lowercase
key = "BLAISE".lower()

def vigenere_decrypt(cipher, key):
    res = []
    ki = 0
    for ch in cipher:
        if ch.lower() in alphabet:
            c_val = alphabet.index(ch.lower())
            k_val = alphabet.index(key[ki % len(key)])
            p_val = (c_val - k_val) % 26
            res.append(alphabet[p_val])
            ki += 1
        else:
            # znaki nieliterowe przepisujemy
            res.append(ch)
    return "".join(res)
hex_bytes = "50,20,75,61,73,61,71,20,71,7a,6a,6d,c5,ba,76,64,6a,70,20,70,77,20,73,72,68,74,65,74,6b,6f,76,20,78,c3,b3,77,71,20,53,64,62,6b,65,74,3f,0a,6d,6b,68,6e,66,3a,2f,2f,63,7a,73,2e,6f,65,33,61,6f,78,2e,66,66,65,2f,71,76,73,76,2f,6f,78,6e,75,6a,63,5f,67,63,70,6d,6a,6b,2e,61,6e,34"
text = bytes.fromhex(hex_bytes.replace(",", "")).decode("utf-8")
print(text)
cipher_text = text;
print(vigenere_decrypt(cipher_text, key))