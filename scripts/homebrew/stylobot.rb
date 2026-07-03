# Homebrew formula for StyloBot - Self-hosted bot detection
# Install: brew install scottgal/stylobot/stylobot
# Or:      brew tap scottgal/stylobot && brew install stylobot

class Stylobot < Formula
  desc "Self-hosted bot detection with 31 detectors, session vectors, and zero PII"
  homepage "https://stylo.bot"
  version "5.6.3"
  license "Unlicense"

  on_macos do
    if Hardware::CPU.arm?
      url "https://github.com/scottgal/stylobot/releases/download/console-v#{version}/stylobot-osx-arm64.tar.gz"
      # sha256 will be filled after release
    else
      url "https://github.com/scottgal/stylobot/releases/download/console-v#{version}/stylobot-osx-x64.tar.gz"
    end
  end

  on_linux do
    if Hardware::CPU.arm?
      url "https://github.com/scottgal/stylobot/releases/download/console-v#{version}/stylobot-linux-arm64.tar.gz"
    else
      url "https://github.com/scottgal/stylobot/releases/download/console-v#{version}/stylobot-linux-x64.tar.gz"
    end
  end

  def install
    bin.install "stylobot"
    # Install config template
    etc.install "appsettings.json" => "stylobot/appsettings.json" if File.exist?("appsettings.json")
  end

  def caveats
    <<~EOS
      StyloBot is installed! Quick start:

        # Start in demo mode (all detectors, verbose logging)
        stylobot

        # Start in production mode with upstream target
        DEFAULT_UPSTREAM=http://localhost:3000 stylobot --mode production

        # Dashboard at http://localhost:5080/_stylobot

      Configuration: #{etc}/stylobot/appsettings.json

      Documentation: https://github.com/scottgal/stylobot
      Commercial tiers: https://stylo.bot/pricing
    EOS
  end

  test do
    # Verify binary runs
    assert_match "Mostlylucid Bot Detection Console Gateway", shell_output("#{bin}/stylobot --help 2>&1", 1)
  end
end
