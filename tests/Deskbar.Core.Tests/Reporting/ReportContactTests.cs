using Deskbar.Core.Reporting;

namespace Deskbar.Core.Tests.Reporting;

// The report window's contact line is optional, and asks once when it is empty or does not look like an address.
// These pin what "looks like an address" means: loose on purpose, since an unrecognised answer costs one click.
public class ReportContactTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_typed_is_missing(string contact) => Assert.Equal(ContactKind.Missing, ReportContact.Check(contact));

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("  first.last+deskbar@mail.co.uk  ")]
    [InlineData("@some_handle")]
    [InlineData("t.me/some_handle")]
    [InlineData("https://t.me/SomeHandle")]
    [InlineData("+995 555 12 34 56")]
    [InlineData("(555) 123-4567")]
    public void Addresses_handles_and_phone_numbers_are_reachable(string contact) =>
        Assert.Equal(ContactKind.Reachable, ReportContact.Check(contact));

    [Theory]
    [InlineData("asdf")]
    [InlineData("none")]
    [InlineData("someone@example")]        // no dot in the domain: a typo, most likely
    [InlineData("someone.example.com")]    // the @ is missing
    [InlineData("someone@@example.com")]
    [InlineData("some one@example.com")]
    [InlineData("@abc")]                   // shorter than a Telegram username can be
    [InlineData("some_handle")]            // a bare word passes for anything, so a handle needs its @
    [InlineData("12345")]                  // too few digits for a phone number
    public void Anything_else_is_unrecognised(string contact) =>
        Assert.Equal(ContactKind.Unrecognised, ReportContact.Check(contact));
}
