cask "parakeetdrop" do
  depends_on arch: :arm64
  depends_on macos: ">= :sequoia"

  version "0.1.3"

  sha256 "0518e882486e59e23e50826d67022f381b7e3079484c981e61bf8c495cdbc94c"

  url "https://github.com/DimonSmart/WhisperDrop/releases/download/v#{version}/ParakeetDrop-v#{version}-osx-arm64-app.zip"

  name "ParakeetDrop"
  desc "Local desktop speech-to-text application powered by Parakeet Ultra"
  homepage "https://github.com/DimonSmart/WhisperDrop"

  app "ParakeetDrop.app"

  caveats <<~EOS
    ParakeetDrop is currently unsigned and not notarized.
    On first launch macOS may require using Open from the Finder context menu.
  EOS
end

