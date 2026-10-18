namespace TaskSpaces.Windows.Audio;

// The one way this app creates Core Audio's device enumerator, shared by SystemVolume (the volume dial) and
// AudioActivity (the sound ring).
//
// By CLSID, into a plain COM object that each caller then casts to ITS OWN interface declaration, and never
// through a [ComImport] class. Measured the hard way: with each file declaring its own enumerator class for
// the same CLSID, the second one to be created failed every time with
//
//   InvalidCastException: Unable to cast object of type 'MMDeviceEnumeratorComObject' to type
//   'MMDeviceEnumeratorComObject'
//
// because the runtime keeps one wrapper per COM object and handed back the one typed as the OTHER file's
// class. A cast to an interface asks the object itself (QueryInterface), so it works whichever wrapper the
// runtime chose. The volume dial worked and the sound ring silently never did, until the poll traced it.
static class CoreAudio
{
    static readonly Guid DeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static object CreateDeviceEnumerator() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(DeviceEnumerator, throwOnError: true)!)!;
}
