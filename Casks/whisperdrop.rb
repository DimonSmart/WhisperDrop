cask "whisperdrop" do
  arch arm: "arm64", intel: "x64"

  version "0.1.2"

  sha256 arm: "5a4a47cd83def69d4b6df923c8c42a165e551c19036db43da394b3bde4a49ce3",
         intel: "df40d57abf2d7b87685be98d5338b72e69e7a1f0d90ace7d2d2507712e36c3fc"

  url "https://github.com/DimonSmart/WhisperDrop/releases/download/v#{version}/WhisperDrop-v#{version}-osx-#{arch}-app.zip"

  name "WhisperDrop"
  desc "Local desktop speech-to-text application powered by Whisper"
  homepage "https://github.com/DimonSmart/WhisperDrop"

  app "WhisperDrop.app"

  caveats <<~EOS
    WhisperDrop is currently unsigned and not notarized.
    On first launch macOS may require using Open from the Finder context menu.
  EOS
end

