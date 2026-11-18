import Foundation
import Security

/// The client secret as a generic password in the login Keychain (account `client-secret`).
public enum Keychain {
    /// The app's Keychain service; tests pass their own.
    public static let defaultService = "org.renkuos.PortsBuilder"
    static let account = "client-secret"

    private static func query(_ service: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    /// - Returns: the stored secret, or nil if there is none (or the Keychain cannot be read).
    public static func readSecret(service: String = defaultService) -> String? {
        var query = query(service)
        query[kSecReturnData as String] = true
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else {
            return nil
        }
        return String(data: data, encoding: .utf8)
    }

    /// Replaces the stored secret.
    /// - Throws: the `OSStatus` as an `NSError` in `NSOSStatusErrorDomain`.
    public static func saveSecret(_ secret: String, service: String = defaultService) throws {
        try deleteSecret(service: service)
        var add = query(service)
        add[kSecValueData as String] = Data(secret.utf8)
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
    }

    /// Removes the stored secret; no secret stored is not an error.
    /// - Throws: the `OSStatus` as an `NSError` in `NSOSStatusErrorDomain`.
    public static func deleteSecret(service: String = defaultService) throws {
        let status = SecItemDelete(query(service) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw NSError(domain: NSOSStatusErrorDomain, code: Int(status))
        }
    }
}
