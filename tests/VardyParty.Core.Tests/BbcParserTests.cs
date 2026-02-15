using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VardyParty.Core.Tests;
using VardyParty.Parsers;
using VardyParty.Services;
using Xunit;

namespace VardyParty.Core.Tests
{
    public class BbcParserTests
    {
        private BbcHtmlParser CreateParser()
        {
             var logger = NullLogger<BbcHtmlParser>.Instance;
             var parser = new BbcJsonParser(NullLogger<BbcJsonParser>.Instance);
             return new BbcHtmlParser(logger, parser);
        }

        [Fact]
        public void Parse_LiverpoolBarnsley_FullTime_Score4_1()
        {
            // Arrange
            // The HTML snippet provided by the user, representing Liverpool 4-1 Barnsley at FT.
            var html = @"<div data-event-id=""s-d1adeg3nqf9m10mkpzoyvbl04"" class=""ssrcss-1bjtunb-GridContainer e1efi6g55""><div data-participant-id=""c8h9bw1l82s06h77xxrelzhur"" class=""ssrcss-bon2fo-WithInlineFallback-TeamHome e1efi6g53""><div class=""ssrcss-1ucldln-StyledTeam-HomeTeam eirdlos1""><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Liverpool</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Liverpool</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Liverpool</span></div><div data-testid=""badge-container-liverpool"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-liverpool"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/football/liverpool.34999937ef.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div></div></div><div class=""ssrcss-y5s079-WithInlineFallback-Scores e1efi6g51""><div class=""ssrcss-etkxi3-StyledCentre ejsemf30""><div data-testid=""score"" aria-hidden=""true"" class=""ssrcss-p1xla2-StyledScore e56kr2l3""><div class=""ssrcss-qsbptj-HomeScore e56kr2l2"">4</div><div class=""ssrcss-82byax-VerticalLine e56kr2l0""></div><div class=""ssrcss-fri5a2-AwayScore e56kr2l1"">1</div></div></div></div><div data-participant-id=""9db4t34qy4owglhvvgwf5l22i"" class=""ssrcss-nvj22c-WithInlineFallback-TeamAway e1efi6g52""><div class=""ssrcss-1d12j2y-StyledTeam-AwayTeam eirdlos0""><div data-testid=""badge-container-barnsley"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-barnsley"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/football/barnsley.96da86ec19.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Barnsley</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Barnsley</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Barnsley</span></div></div></div><div class=""ssrcss-xxm013-MatchProgressContainer e1efi6g50""><div class=""ssrcss-1qnx7n2-MatchProgressWrapper ep2tsh21""><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Full time</span><div aria-hidden=""true"" class=""ssrcss-msb9pu-StyledPeriod e307mhr0""><div>FT</div></div></div></div></div>";

            // Act
            var fixtures = CreateParser().ParseHtml(html);

            // Assert
            Assert.NotNull(fixtures);
            Assert.Single(fixtures);

            var fixture = fixtures.First();
            
            Assert.Equal("Liverpool", fixture.Home);
            Assert.Equal("Barnsley", fixture.Away);
            Assert.Equal(4, fixture.HomeScore);
            Assert.Equal(1, fixture.AwayScore);
            Assert.True(fixture.IsFinished, "Should be finished");
            Assert.Equal("FT", fixture.Status);
            Assert.Contains("liverpool.34999937ef.svg", fixture.HomeBadgeUrl);
            Assert.Contains("barnsley.96da86ec19.svg", fixture.AwayBadgeUrl);
        }

        [Fact]
        public void Parse_HamburgLeverkusen_Postponed()
        {
            // Arrange
            var html = @"<div data-event-id=""s-ej9algl988lk7c7603bl793pw"" class=""ssrcss-1bjtunb-GridContainer e1efi6g55""><div data-participant-id=""75xi6hloabmnjn2kzgj1g8h1s"" class=""ssrcss-bon2fo-WithInlineFallback-TeamHome e1efi6g53""><div class=""ssrcss-1ucldln-StyledTeam-HomeTeam eirdlos1""><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Hamburg</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Hamburger SV</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Hamburger SV</span></div><div data-testid=""badge-container-hamburg"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-hamburg"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/football/hamburg.a69019ac4b.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div></div></div><div class=""ssrcss-y5s079-WithInlineFallback-Scores e1efi6g51""><div class=""ssrcss-etkxi3-StyledCentre ejsemf30""><div data-testid=""score"" aria-hidden=""true"" class=""ssrcss-pmqp1j-StyledScore e56kr2l3""><div class=""ssrcss-qsbptj-HomeScore e56kr2l2"">P</div><div class=""ssrcss-6ucl1t-VerticalLine e56kr2l0""></div><div class=""ssrcss-fri5a2-AwayScore e56kr2l1"">P</div></div></div></div><div data-participant-id=""7ad69ngbpjuyzv96drf8d9sn2"" class=""ssrcss-nvj22c-WithInlineFallback-TeamAway e1efi6g52""><div class=""ssrcss-1d12j2y-StyledTeam-AwayTeam eirdlos0""><div data-testid=""badge-container-bayer-leverkusen"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-bayer-leverkusen"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/football/bayer-leverkusen.b1c0805fcd.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Leverkusen</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Bayer Leverkusen</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Bayer Leverkusen</span></div></div></div><div class=""ssrcss-xxm013-MatchProgressContainer e1efi6g50""><div class=""ssrcss-1qnx7n2-MatchProgressWrapper ep2tsh21""><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Match Postponed</span><div aria-hidden=""true"" class=""ssrcss-msb9pu-StyledPeriod e307mhr0""><div>Match Postponed</div></div></div></div></div>";

            // Act
            var fixtures = CreateParser().ParseHtml(html);

            // Assert
            Assert.NotNull(fixtures);
            Assert.Single(fixtures);

            var fixture = fixtures.First();
            
            Assert.Equal("Hamburger SV", fixture.Home);
            Assert.Equal("Bayer Leverkusen", fixture.Away);
            Assert.Null(fixture.HomeScore);
            Assert.Null(fixture.AwayScore);
            Assert.False(fixture.IsFinished);
            Assert.False(fixture.IsInProgress);
            Assert.Equal("Postponed", fixture.Status);
        }

        [Fact]
        public void Parse_CulturalLeonesaAthletic_AET_Finished()
        {
            // Arrange
            var html = @"<div data-event-id=""s-2qx844dk3zbcm1lmyy0me3c44"" class=""ssrcss-1bjtunb-GridContainer e1efi6g55""><div data-participant-id=""4szvjv7xibphumticc9438w2g"" class=""ssrcss-bon2fo-WithInlineFallback-TeamHome e1efi6g53""><div class=""ssrcss-1ucldln-StyledTeam-HomeTeam eirdlos1""><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Cultural</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Cultural Leonesa</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Cultural Leonesa</span></div><div data-testid=""badge-container-undefined"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-undefined"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/placeholders/placeholder-badge.77f47c4009.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div></div></div><div class=""ssrcss-y5s079-WithInlineFallback-Scores e1efi6g51""><div class=""ssrcss-etkxi3-StyledCentre ejsemf30""><div data-testid=""score"" aria-hidden=""true"" class=""ssrcss-p1xla2-StyledScore e56kr2l3""><div class=""ssrcss-qsbptj-HomeScore e56kr2l2"">3</div><div class=""ssrcss-82byax-VerticalLine e56kr2l0""></div><div class=""ssrcss-fri5a2-AwayScore e56kr2l1"">4</div></div></div></div><div data-participant-id=""3czravw89omgc9o4s0w3l1bg5"" class=""ssrcss-nvj22c-WithInlineFallback-TeamAway e1efi6g52""><div class=""ssrcss-1d12j2y-StyledTeam-AwayTeam eirdlos0""><div data-testid=""badge-container-athletic-bilbao"" class=""ssrcss-161yplk-BadgeContainer ezmsq4q1""><img alt="""" data-testid=""badge-img-athletic-bilbao"" src=""https://static.files.bbci.co.uk/core/website/assets/static/sport/football/athletic-bilbao.d8dcd1124f.svg"" aria-hidden=""true"" class=""ssrcss-1knyx38-BadgeImage ezmsq4q0""></div><div class=""ssrcss-1upu8z0-TeamNameWrapper emlpoi32""><span aria-hidden=""true"" class=""ssrcss-c8w0oz-MobileValue emlpoi31"">Athletic</span><span aria-hidden=""true"" class=""ssrcss-1p14tic-DesktopValue emlpoi30"">Athletic Club</span><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">Athletic Club</span></div></div></div><div class=""ssrcss-xxm013-MatchProgressContainer e1efi6g50""><div class=""ssrcss-1qnx7n2-MatchProgressWrapper ep2tsh21""><span class=""visually-hidden ssrcss-1f39n02-VisuallyHidden e16en2lz0"">After extra time</span><div aria-hidden=""true"" class=""ssrcss-msb9pu-StyledPeriod e307mhr0""><div>AET</div></div></div></div></div>";

            // Act
            var fixtures = CreateParser().ParseHtml(html);

            // Assert
            Assert.NotNull(fixtures);
            Assert.Single(fixtures);

            var fixture = fixtures.First();
            
            Assert.Equal("Cultural Leonesa", fixture.Home);
            Assert.Equal("Athletic Club", fixture.Away);
            Assert.Equal(3, fixture.HomeScore);
            Assert.Equal(4, fixture.AwayScore);
            Assert.True(fixture.IsFinished, "Should be IsFinished=true for AET");
            Assert.False(fixture.IsInProgress, "Should be IsInProgress=false for AET");
            Assert.Equal("AET", fixture.Status); 
        }
    }
}
