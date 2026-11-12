# Bootstrap metadata only. The Homebrew workflow replaces this after publication.
cask "parakeetdrop" do
  version "0.1.0"
  sha256 :no_check

  url "https://github.com/DimonSmart/WhisperDrop/releases/download/v#{version}/ParakeetDrop-v#{version}-osx-arm64-app.zip"
  name "ParakeetDrop"
  desc "Local desktop speech-to-text application powered by Parakeet Ultra"
  homepage "https://github.com/DimonSmart/WhisperDrop"

  depends_on arch: :arm64
  depends_on macos: ">= :sequoia"

  app "ParakeetDrop.app"

  caveats <<~EOS
    ParakeetDrop is currently unsigned and not notarized.
    On first launch macOS may require using Open from the Finder context menu.
  EOS
end
