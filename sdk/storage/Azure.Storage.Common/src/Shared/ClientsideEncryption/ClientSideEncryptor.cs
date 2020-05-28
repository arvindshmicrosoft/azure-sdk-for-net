// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core.Cryptography;
using Azure.Storage.Cryptography.Models;

namespace Azure.Storage.Cryptography
{
    internal static class ClientSideEncryptor
    {
        /// <summary>
        /// Wraps the given read-stream in a CryptoStream and provides the metadata used to create
        /// that stream.
        /// </summary>
        /// <param name="plaintext">Stream to wrap.</param>
        /// <param name="keyWrapper">Key encryption key (KEK).</param>
        /// <param name="keyWrapAlgorithm">Algorithm to encrypt the content encryption key (CEK) with.</param>
        /// <param name="async">Whether to wrap the CEK asynchronously.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The wrapped stream to read from and the encryption metadata for the wrapped stream.</returns>
        public static async Task<(Stream ciphertext, EncryptionData encryptionData)> EncryptInternal(
            Stream plaintext,
            IKeyEncryptionKey keyWrapper,
            string keyWrapAlgorithm,
            bool async,
            CancellationToken cancellationToken)
        {
            var generatedKey = CreateKey(EncryptionConstants.EncryptionKeySizeBits);
            EncryptionData encryptionData = default;
            Stream ciphertext = default;

            using (AesCryptoServiceProvider aesProvider = new AesCryptoServiceProvider() { Key = generatedKey })
            {
                encryptionData = await EncryptionData.CreateInternalV1_0(
                    contentEncryptionIv: aesProvider.IV,
                    keyWrapAlgorithm: keyWrapAlgorithm,
                    contentEncryptionKey: generatedKey,
                    keyEncryptionKey: keyWrapper,
                    async: async,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                ciphertext = new CryptoStream(
                    plaintext,
                    aesProvider.CreateEncryptor(),
                    CryptoStreamMode.Read);
            }

            return (ciphertext, encryptionData);
        }

        /// <summary>
        /// Encrypts the given stream and provides the metadata used to encrypt. This method writes to a memory stream,
        /// optimized for known-size data that will already be buffered in memory.
        /// </summary>
        /// <param name="plaintext">Stream to encrypt.</param>
        /// <param name="keyWrapper">Key encryption key (KEK).</param>
        /// <param name="keyWrapAlgorithm">Algorithm to encrypt the content encryption key (CEK) with.</param>
        /// <param name="async">Whether to wrap the CEK asynchronously.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The encrypted data and the encryption metadata for the wrapped stream.</returns>
        public static async Task<(byte[] ciphertext, EncryptionData encryptionData)> BufferedEncryptInternal(
            Stream plaintext,
            IKeyEncryptionKey keyWrapper,
            string keyWrapAlgorithm,
            bool async,
            CancellationToken cancellationToken)
        {
            var generatedKey = CreateKey(EncryptionConstants.EncryptionKeySizeBits);
            EncryptionData encryptionData = default;
            var ciphertext = new MemoryStream();
            byte[] bufferedCiphertext = default;

            using (AesCryptoServiceProvider aesProvider = new AesCryptoServiceProvider() { Key = generatedKey })
            {
                encryptionData = await EncryptionData.CreateInternalV1_0(
                    contentEncryptionIv: aesProvider.IV,
                    keyWrapAlgorithm: keyWrapAlgorithm,
                    contentEncryptionKey: generatedKey,
                    keyEncryptionKey: keyWrapper,
                    async: async,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var transformStream = new CryptoStream(
                    ciphertext,
                    aesProvider.CreateEncryptor(),
                    CryptoStreamMode.Write);

                if (async)
                {
                    await plaintext.CopyToAsync(transformStream).ConfigureAwait(false);
                }
                else
                {
                    plaintext.CopyTo(transformStream);
                }

                transformStream.FlushFinalBlock();

                bufferedCiphertext = ciphertext.ToArray();
            }

            return (bufferedCiphertext, encryptionData);
        }

        /// <summary>
        /// Securely generate a key.
        /// </summary>
        /// <param name="numBits">Key size.</param>
        /// <returns>The generated key bytes.</returns>
        private static byte[] CreateKey(int numBits)
        {
            using (var secureRng = new RNGCryptoServiceProvider())
            {
                var buff = new byte[numBits / 8];
                secureRng.GetBytes(buff);
                return buff;
            }
        }
    }
}
