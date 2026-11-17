namespace DatabaseInterchange.Format;

/// <summary>
/// Delegate for format validation functions — the header assertion a
/// <see cref="FormatClass.Validated"/> or <see cref="FormatClass.Probe"/> entry adds to its magic.
/// </summary>
/// <remarks>
/// Every check reads only inside the probe budget (<see cref="BlobFormat.ProbeBytes"/>), which is what lets
/// naming a large value cost a constant rather than a fraction of the value.
/// </remarks>
/// <param name="probe">The first bytes of the value, up to the probe budget.</param>
/// <param name="length">The number of valid bytes in <paramref name="probe"/>.</param>
/// <returns><see langword="true"/> when the bytes carry the signature the entry claims.</returns>
internal delegate bool FormatCheck(byte[] probe, int length);
